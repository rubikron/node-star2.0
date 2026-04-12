using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SwBridge.Models;

/// <summary>
/// Represents a compact snapshot of the current SOLIDWORKS session state.
/// The snapshot is serialized and passed to the LLM so it can reason about
/// the active document context without unnecessary token overhead.
/// </summary>
public sealed record SwState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Gets the deterministic token for this snapshot.</summary>
    [JsonPropertyName("snapshot")]
    public string SnapshotToken { get; init; } = string.Empty;

    /// <summary>Gets the connected SOLIDWORKS revision number when available.</summary>
    [JsonPropertyName("SwVersion")]
    public string? RevisionNumber { get; init; }

    /// <summary>Gets the active document summary, or <see langword="null"/> if no document is active.</summary>
    [JsonPropertyName("activeDoc")]
    public SwDocumentState? ActiveDocument { get; init; }

    /// <summary>Gets the currently open documents in deterministic order (with full model data).</summary>
    [JsonPropertyName("documents")]
    public IReadOnlyList<SwDocumentDetail> OpenDocuments { get; init; } = [];

    /// <summary>Gets the current selection summaries in deterministic order.</summary>
    [JsonPropertyName("selection")]
    public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

    /// <summary>Gets the active configuration name for the active document when available.</summary>
    [JsonPropertyName("activeConfig")]
    public string? ActiveConfigurationName { get; init; }

    /// <summary>
    /// Creates a normalized snapshot with a deterministic token.
    /// </summary>
    public static SwState Create(
        string? revisionNumber,
        SwDocumentState? activeDocument,
        IEnumerable<SwDocumentDetail>? openDocuments,
        IEnumerable<SwSelectionState>? selection,
        string? activeConfigurationName)
    {
        var normalized = new SwState
        {
            RevisionNumber = Normalize(revisionNumber),
            ActiveDocument = activeDocument,
            OpenDocuments = (openDocuments ?? [])
                .OrderBy(static doc => doc.Id, StringComparer.Ordinal)
                .ToArray(),
            Selection = (selection ?? [])
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray(),
            ActiveConfigurationName = Normalize(activeConfigurationName)
        };

        return normalized with
        {
            SnapshotToken = ComputeSnapshotToken(normalized)
        };
    }

    /// <summary>Serializes the snapshot into compact JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Serializes a full-state tool response containing this snapshot.
    /// Root level: mode, snapshot, SwVersion.
    /// Under "state": activeDoc, selection, activeConfig, documents (with full model data).
    /// </summary>
    public string ToFullResponseJson() =>
        JsonSerializer.Serialize(
            new SwFullSnapshot
            {
                SnapshotToken = SnapshotToken,
                SwVersion     = RevisionNumber,
                State         = new SwSessionContext
                {
                    ActiveDocument         = ActiveDocument,
                    Selection              = Selection,
                    ActiveConfigurationName = ActiveConfigurationName,
                    Documents              = OpenDocuments
                }
            },
            JsonOptions);

    /// <summary>Builds a compact patch from a previous snapshot to the current one.</summary>
    public SwStatePatch BuildPatch(SwState? previous)
    {
        var previousSnapshotToken = previous?.SnapshotToken;
        previous ??= Create(null, null, null, null, null);

        var patch = new SwStatePatch
        {
            PreviousSnapshotToken = previousSnapshotToken,
            SnapshotToken         = SnapshotToken
        };

        if (!string.Equals(previous.RevisionNumber, RevisionNumber, StringComparison.Ordinal))
            patch.RevisionNumber = RevisionNumber;

        if (previous.ActiveDocument != ActiveDocument)
            patch.ActiveDocument = ActiveDocument;

        if (!string.Equals(previous.ActiveConfigurationName, ActiveConfigurationName, StringComparison.Ordinal))
            patch.ActiveConfigurationName = ActiveConfigurationName;

        if (!previous.Selection.SequenceEqual(Selection))
            patch.Selection = Selection;

        var documentPatch = SwDocumentCollectionPatch.Create(previous.OpenDocuments, OpenDocuments);
        if (!documentPatch.IsEmpty)
            patch.OpenDocuments = documentPatch;

        return patch;
    }

    /// <summary>Serializes a patch response from a previous snapshot to the current one.</summary>
    public string ToPatchResponseJson(SwState? previous) =>
        JsonSerializer.Serialize(BuildPatch(previous), JsonOptions);

    /// <summary>
    /// Deserializes a snapshot from the current full response envelope.
    /// Handles both the new root-level shape and the legacy wrapped shape.
    /// </summary>
    public static SwState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root)
                return null;

            // New shape: snapshot at root alongside mode/SwVersion
            if (root["snapshot"] is not null && root["state"] is JsonObject)
            {
                // Reconstruct SwState from the flattened root + state object
                var stateNode = root["state"]!.AsObject();
                return new SwState
                {
                    SnapshotToken           = root["snapshot"]?.GetValue<string>() ?? string.Empty,
                    RevisionNumber          = root["SwVersion"]?.GetValue<string>(),
                    ActiveDocument          = stateNode["activeDoc"]?.Deserialize<SwDocumentState>(JsonOptions),
                    OpenDocuments           = stateNode["documents"]?.Deserialize<SwDocumentDetail[]>(JsonOptions) ?? [],
                    Selection               = stateNode["selection"]?.Deserialize<SwSelectionState[]>(JsonOptions) ?? [],
                    ActiveConfigurationName = stateNode["activeConfig"]?.GetValue<string>()
                };
            }

            // Legacy shape: state wraps the full SwState object
            if (root["state"] is JsonNode legacyStateNode)
                return legacyStateNode.Deserialize<SwState>(JsonOptions);

            return root.Deserialize<SwState>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ComputeSnapshotToken(SwState state)
    {
        var payload = JsonSerializer.Serialize(
            new SnapshotTokenPayload
            {
                RevisionNumber          = state.RevisionNumber,
                ActiveDocument          = state.ActiveDocument,
                DocumentModelTokens     = string.Join(",", state.OpenDocuments.Select(d => $"{d.Id}:{d.ModelSnapshot}")),
                Selection               = state.Selection,
                ActiveConfigurationName = state.ActiveConfigurationName
            },
            JsonOptions);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record SnapshotTokenPayload
    {
        [JsonPropertyName("SwVersion")]
        public string? RevisionNumber { get; init; }

        [JsonPropertyName("activeDoc")]
        public SwDocumentState? ActiveDocument { get; init; }

        [JsonPropertyName("docTokens")]
        public string? DocumentModelTokens { get; init; }

        [JsonPropertyName("selection")]
        public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

        [JsonPropertyName("activeConfig")]
        public string? ActiveConfigurationName { get; init; }
    }
}

// ── Response envelope records ─────────────────────────────────────────────────

/// <summary>
/// Full-state response envelope: mode, snapshot, SwVersion at root;
/// session context (activeDoc, selection, activeConfig, documents) under "state".
/// </summary>
public sealed record SwFullSnapshot
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "full";

    [JsonPropertyName("snapshot")]
    public string SnapshotToken { get; init; } = string.Empty;

    [JsonPropertyName("SwVersion")]
    public string? SwVersion { get; init; }

    [JsonPropertyName("state")]
    public SwSessionContext State { get; init; } = new();
}

/// <summary>Session context: active document reference, selection, config, and full document list.</summary>
public sealed record SwSessionContext
{
    [JsonPropertyName("activeDoc")]
    public SwDocumentState? ActiveDocument { get; init; }

    [JsonPropertyName("selection")]
    public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

    [JsonPropertyName("activeConfig")]
    public string? ActiveConfigurationName { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<SwDocumentDetail> Documents { get; init; } = [];
}

// ── Data records ──────────────────────────────────────────────────────────────

/// <summary>
/// Compact document reference used for the <c>activeDoc</c> field.
/// Contains only session metadata (id, title, path, type, config).
/// </summary>
public sealed record SwDocumentState(
    [property: JsonPropertyName("id")]     string  Id,
    [property: JsonPropertyName("title")]  string? Title,
    [property: JsonPropertyName("path")]   string? Path,
    [property: JsonPropertyName("type")]   string  DocumentType,
    [property: JsonPropertyName("config")] string? ConfigurationName);

/// <summary>
/// Full document entry combining session metadata with model data.
/// Used in the <c>documents</c> array.
/// For parts: <see cref="Features"/> is populated; <see cref="Components"/> and <see cref="Mates"/> are null.
/// For assemblies: <see cref="Components"/> and <see cref="Mates"/> are populated; <see cref="Features"/> is null.
/// </summary>
public sealed record SwDocumentDetail
{
    // Session metadata
    [JsonPropertyName("id")]     public string  Id               { get; init; } = string.Empty;
    [JsonPropertyName("title")]  public string? Title            { get; init; }
    [JsonPropertyName("path")]   public string? Path             { get; init; }
    [JsonPropertyName("type")]   public string  DocumentType     { get; init; } = string.Empty;
    [JsonPropertyName("config")] public string? ConfigurationName { get; init; }

    // Model snapshot token (for change detection in patch diffs)
    [JsonPropertyName("snapshot")] public string ModelSnapshot { get; init; } = string.Empty;

    // Geometry
    [JsonPropertyName("unsaved")]   public bool      Unsaved   { get; init; }
    [JsonPropertyName("boundsMm")]  public double[]? BoundsMm  { get; init; }
    [JsonPropertyName("massG")]     public double?   MassGrams { get; init; }
    [JsonPropertyName("volumeMm3")] public double?   VolumeMm3 { get; init; }

    // Part-specific model data (null for assemblies and drawings)
    [JsonPropertyName("features")]   public IReadOnlyList<FeatureInfo>?   Features   { get; init; }

    // Assembly-specific model data (null for parts and drawings)
    [JsonPropertyName("components")] public IReadOnlyList<ComponentInfo>? Components { get; init; }
    [JsonPropertyName("mates")]      public IReadOnlyList<MateInfo>?      Mates      { get; init; }
}

/// <summary>Represents a compact summary of one selected entity in the active document.</summary>
public sealed record SwSelectionState(
    [property: JsonPropertyName("key")]        string  Key,
    [property: JsonPropertyName("documentId")] string  DocumentId,
    [property: JsonPropertyName("type")]       string  SelectionType,
    [property: JsonPropertyName("name")]       string? Name,
    [property: JsonPropertyName("mark")]       int?    Mark);

/// <summary>Represents a compact patch response between two snapshots.</summary>
public sealed record SwStatePatch
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "patch";

    [JsonPropertyName("prevSnap")]
    public string? PreviousSnapshotToken { get; set; }

    [JsonPropertyName("snapshot")]
    public string SnapshotToken { get; set; } = string.Empty;

    [JsonPropertyName("SwVersion")]
    public string? RevisionNumber { get; set; }

    [JsonPropertyName("activeDoc")]
    public SwDocumentState? ActiveDocument { get; set; }

    [JsonPropertyName("documents")]
    public SwDocumentCollectionPatch? OpenDocuments { get; set; }

    [JsonPropertyName("selection")]
    public IReadOnlyList<SwSelectionState>? Selection { get; set; }

    [JsonPropertyName("activeConfig")]
    public string? ActiveConfigurationName { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        RevisionNumber is null &&
        ActiveDocument is null &&
        OpenDocuments is null &&
        Selection is null &&
        ActiveConfigurationName is null;
}

/// <summary>Represents changes to the open-document collection.</summary>
public sealed record SwDocumentCollectionPatch
{
    [JsonPropertyName("added")]
    public IReadOnlyList<SwDocumentDetail>? Added { get; init; }

    [JsonPropertyName("updated")]
    public IReadOnlyList<SwDocumentPatch>? Updated { get; init; }

    [JsonPropertyName("removed")]
    public IReadOnlyList<string>? Removed { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        Added is null &&
        Updated is null &&
        Removed is null;

    /// <summary>
    /// Computes a collection patch between previous and current document sets.
    /// Detects changes by document id (presence/absence) and model snapshot token (content changes).
    /// </summary>
    public static SwDocumentCollectionPatch Create(
        IReadOnlyList<SwDocumentDetail> previous,
        IReadOnlyList<SwDocumentDetail> current)
    {
        var previousMap = previous.ToDictionary(static doc => doc.Id, StringComparer.Ordinal);
        var currentMap  = current.ToDictionary(static doc => doc.Id, StringComparer.Ordinal);

        var added = current
            .Where(doc => !previousMap.ContainsKey(doc.Id))
            .ToArray();

        var updated = current
            .Select(doc => previousMap.TryGetValue(doc.Id, out var earlier)
                ? SwDocumentPatch.Compute(earlier, doc)
                : null)
            .OfType<SwDocumentPatch>()
            .ToArray();

        var removed = previous
            .Select(static doc => doc.Id)
            .Where(id => !currentMap.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        return new SwDocumentCollectionPatch
        {
            Added   = added.Length   == 0 ? null : added,
            Updated = updated.Length == 0 ? null : updated,
            Removed = removed.Length == 0 ? null : removed
        };
    }
}

// ── Document-level patch records ──────────────────────────────────────────────

/// <summary>
/// Fine-grained patch for a single document whose model content changed.
/// Only fields that actually changed are present; absent fields are unchanged.
/// </summary>
public sealed record SwDocumentPatch
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    [JsonPropertyName("id")]       public string Id       { get; init; } = string.Empty;
    [JsonPropertyName("snapshot")] public string Snapshot { get; init; } = string.Empty;

    // Scalar fields — null means "unchanged"
    [JsonPropertyName("boundsMm")]  public double[]? BoundsMm  { get; init; }
    [JsonPropertyName("massG")]     public double?   MassGrams { get; init; }
    [JsonPropertyName("volumeMm3")] public double?   VolumeMm3 { get; init; }
    [JsonPropertyName("unsaved")]   public bool?     Unsaved   { get; init; }

    // Collection diffs — null means "no changes in this collection"
    [JsonPropertyName("features")]   public SwListDiff<FeatureInfo>?   Features   { get; init; }
    [JsonPropertyName("components")] public SwListDiff<ComponentInfo>? Components { get; init; }
    [JsonPropertyName("mates")]      public SwListDiff<MateInfo>?      Mates      { get; init; }

    /// <summary>
    /// Computes a fine-grained patch between two versions of the same document.
    /// Returns <see langword="null"/> if the model snapshot token is unchanged.
    /// </summary>
    public static SwDocumentPatch? Compute(SwDocumentDetail previous, SwDocumentDetail current)
    {
        if (string.Equals(previous.ModelSnapshot, current.ModelSnapshot, StringComparison.Ordinal))
            return null;

        return new SwDocumentPatch
        {
            Id         = current.Id,
            Snapshot   = current.ModelSnapshot,
            BoundsMm   = JsonEquals(previous.BoundsMm,  current.BoundsMm)  ? null : current.BoundsMm,
            MassGrams  = previous.MassGrams == current.MassGrams            ? null : current.MassGrams,
            VolumeMm3  = previous.VolumeMm3 == current.VolumeMm3            ? null : current.VolumeMm3,
            Unsaved    = previous.Unsaved   == current.Unsaved              ? null : current.Unsaved,
            Features   = DiffList(previous.Features,   current.Features,   static f => f.Name),
            Components = DiffList(previous.Components, current.Components, static c => c.Name),
            Mates      = DiffList(previous.Mates,      current.Mates,      static m => m.Name),
        };
    }

    private static SwListDiff<T>? DiffList<T>(
        IReadOnlyList<T>? previous,
        IReadOnlyList<T>? current,
        Func<T, string>   keyOf)
    {
        var prev = previous ?? [];
        var curr = current  ?? [];

        var prevMap = prev.ToDictionary(keyOf, StringComparer.Ordinal);
        var currMap = curr.ToDictionary(keyOf, StringComparer.Ordinal);

        var added   = curr.Where(x => !prevMap.ContainsKey(keyOf(x))).ToArray();
        var removed = prev.Select(keyOf).Where(k => !currMap.ContainsKey(k)).ToArray();
        var updated = curr.Where(x =>
            prevMap.TryGetValue(keyOf(x), out var p) && !JsonEquals(x, p)).ToArray();

        if (added.Length == 0 && updated.Length == 0 && removed.Length == 0)
            return null;

        return new SwListDiff<T>
        {
            Added   = added.Length   == 0 ? null : added,
            Updated = updated.Length == 0 ? null : updated,
            Removed = removed.Length == 0 ? null : removed
        };
    }

    private static bool JsonEquals<T>(T? a, T? b) =>
        JsonSerializer.Serialize(a, CompactOptions) ==
        JsonSerializer.Serialize(b, CompactOptions);
}

/// <summary>Generic add/update/remove diff for a named collection within a document.</summary>
public sealed record SwListDiff<T>
{
    [JsonPropertyName("added")]   public IReadOnlyList<T>?      Added   { get; init; }
    [JsonPropertyName("updated")] public IReadOnlyList<T>?      Updated { get; init; }
    [JsonPropertyName("removed")] public IReadOnlyList<string>? Removed { get; init; }

    [JsonIgnore] public bool IsEmpty => Added is null && Updated is null && Removed is null;
}
