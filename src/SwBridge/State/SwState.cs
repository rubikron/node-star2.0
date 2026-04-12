using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SwBridge.Models;

/// <summary>
/// Represents a compact snapshot of the current SOLIDWORKS session state.
/// </summary>
public sealed record SwState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [JsonPropertyName("snapshot")]
    public string SnapshotToken { get; init; } = string.Empty;

    [JsonPropertyName("SwVersion")]
    public string? RevisionNumber { get; init; }

    [JsonPropertyName("activeDoc")]
    public SwDocumentState? ActiveDocument { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<SwDocumentDetail> OpenDocuments { get; init; } = [];

    [JsonPropertyName("selection")]
    public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

    [JsonPropertyName("activeConfig")]
    public string? ActiveConfigurationName { get; init; }

    public static SwState Create(
        string? revisionNumber,
        SwDocumentState? activeDocument,
        IEnumerable<SwDocumentDetail>? openDocuments,
        IEnumerable<SwSelectionState>? selection,
        string? activeConfigurationName)
    {
        var normalized = new SwState
        {
            RevisionNumber          = Normalize(revisionNumber),
            ActiveDocument          = activeDocument,
            OpenDocuments           = (openDocuments ?? []).OrderBy(static d => d.Id, StringComparer.Ordinal).ToArray(),
            Selection               = (selection ?? []).OrderBy(static s => s.Key, StringComparer.Ordinal).ToArray(),
            ActiveConfigurationName = Normalize(activeConfigurationName)
        };
        return normalized with { SnapshotToken = ComputeSnapshotToken(normalized) };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Full response: mode/snapshot/SwVersion at root; session context under "state".
    /// </summary>
    public string ToFullResponseJson() =>
        JsonSerializer.Serialize(
            new SwFullSnapshot
            {
                SnapshotToken = SnapshotToken,
                SwVersion     = RevisionNumber,
                State         = new SwSessionContext
                {
                    ActiveDocument          = ActiveDocument,
                    Selection               = Selection,
                    ActiveConfigurationName = ActiveConfigurationName,
                    Documents               = OpenDocuments
                }
            },
            JsonOptions);

    /// <summary>
    /// Patch response: mode/prevSnap/snapshot at root; all changes nested under "state".
    /// Session-level changes (activeDoc, selection, activeConfig) appear as items in state.updated.
    /// Only fields that actually changed are present.
    /// </summary>
    public SwStatePatch BuildPatch(SwState? previous)
    {
        var previousSnapshotToken = previous?.SnapshotToken;
        previous ??= Create(null, null, null, null, null);

        var added   = new List<SwChangeItem>();
        var updated = new List<SwChangeItem>();
        var removed = new List<SwChangeItem>();

        // Session-level changes → updated items
        if (previous.ActiveDocument != ActiveDocument)
            updated.Add(new SwChangeItem { ActiveDocument = ActiveDocument });
        if (!string.Equals(previous.ActiveConfigurationName, ActiveConfigurationName, StringComparison.Ordinal))
            updated.Add(new SwChangeItem { ActiveConfigurationName = ActiveConfigurationName });
        if (!previous.Selection.SequenceEqual(Selection))
            updated.Add(new SwChangeItem { Selection = Selection });

        var prevMap = previous.OpenDocuments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var currMap = OpenDocuments.ToDictionary(d => d.Id, StringComparer.Ordinal);

        // Newly opened documents — full detail
        foreach (var doc in OpenDocuments.Where(d => !prevMap.ContainsKey(d.Id)))
            added.Add(new SwChangeItem { Document = doc });

        // Closed documents — id only
        foreach (var id in previous.OpenDocuments.Select(d => d.Id).Where(id => !currMap.ContainsKey(id)))
            removed.Add(new SwChangeItem { DocumentId = id });

        // Changed documents — fine-grained sub-item diff
        foreach (var doc in OpenDocuments)
        {
            if (!prevMap.TryGetValue(doc.Id, out var prev)) continue;
            if (string.Equals(prev.ModelSnapshot, doc.ModelSnapshot, StringComparison.Ordinal)) continue;

            // Geometry scalars
            var geo = BuildGeometryUpdate(doc.Id, prev, doc);
            if (geo is not null) updated.Add(geo);

            // Mates
            DiffItems(prev.Mates, doc.Mates, m => m.Name,
                m    => new SwChangeItem { DocId = doc.Id, Mate      = m    },
                name => new SwChangeItem { DocId = doc.Id, MateName  = name },
                added, updated, removed);

            // Features
            DiffItems(prev.Features, doc.Features, f => f.Name,
                f    => new SwChangeItem { DocId = doc.Id, Feature      = f    },
                name => new SwChangeItem { DocId = doc.Id, FeatureName  = name },
                added, updated, removed);

            // Components
            DiffItems(prev.Components, doc.Components, c => c.Name,
                c    => new SwChangeItem { DocId = doc.Id, Component      = c    },
                name => new SwChangeItem { DocId = doc.Id, ComponentName  = name },
                added, updated, removed);
        }

        SwPatchState? state = null;
        if (added.Count > 0 || updated.Count > 0 || removed.Count > 0)
        {
            state = new SwPatchState
            {
                Added   = added.Count   > 0 ? added   : null,
                Updated = updated.Count > 0 ? updated : null,
                Removed = removed.Count > 0 ? removed : null
            };
        }

        return new SwStatePatch
        {
            PreviousSnapshotToken = previousSnapshotToken,
            SnapshotToken         = SnapshotToken,
            State                 = state
        };
    }

    public string ToPatchResponseJson(SwState? previous) =>
        JsonSerializer.Serialize(BuildPatch(previous), JsonOptions);

    /// <summary>
    /// Parses a snapshot from a prior full-response envelope.
    /// Handles both the current shape (snapshot at root) and legacy shape (snapshot inside state).
    /// </summary>
    public static SwState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root) return null;

            if (root["snapshot"] is not null && root["state"] is JsonObject stateObj)
            {
                return new SwState
                {
                    SnapshotToken           = root["snapshot"]?.GetValue<string>() ?? string.Empty,
                    RevisionNumber          = root["SwVersion"]?.GetValue<string>(),
                    ActiveDocument          = stateObj["activeDoc"]?.Deserialize<SwDocumentState>(JsonOptions),
                    OpenDocuments           = stateObj["documents"]?.Deserialize<SwDocumentDetail[]>(JsonOptions) ?? [],
                    Selection               = stateObj["selection"]?.Deserialize<SwSelectionState[]>(JsonOptions) ?? [],
                    ActiveConfigurationName = stateObj["activeConfig"]?.GetValue<string>()
                };
            }

            if (root["state"] is JsonNode legacyStateNode)
                return legacyStateNode.Deserialize<SwState>(JsonOptions);

            return root.Deserialize<SwState>(JsonOptions);
        }
        catch (JsonException) { return null; }
    }

    // ── Diff helpers ──────────────────────────────────────────────────────────

    private static SwChangeItem? BuildGeometryUpdate(
        string docId, SwDocumentDetail prev, SwDocumentDetail curr)
    {
        double[]? bounds  = JsonEquals(prev.BoundsMm, curr.BoundsMm) ? null : curr.BoundsMm;
        double?   mass    = prev.MassGrams == curr.MassGrams          ? null : curr.MassGrams;
        double?   vol     = prev.VolumeMm3 == curr.VolumeMm3          ? null : curr.VolumeMm3;
        bool?     unsaved = prev.Unsaved   == curr.Unsaved             ? null : (bool?)curr.Unsaved;

        return bounds is null && mass is null && vol is null && unsaved is null ? null
            : new SwChangeItem
            {
                DocId       = docId,
                DocSnapshot = curr.ModelSnapshot,
                BoundsMm    = bounds,
                MassGrams   = mass,
                VolumeMm3   = vol,
                Unsaved     = unsaved
            };
    }

    private static void DiffItems<T>(
        IReadOnlyList<T>? previous,
        IReadOnlyList<T>? current,
        Func<T, string> keyOf,
        Func<T, SwChangeItem> wrapPayload,
        Func<string, SwChangeItem> wrapRemoval,
        List<SwChangeItem> added,
        List<SwChangeItem> updated,
        List<SwChangeItem> removed)
    {
        var prev = previous ?? (IReadOnlyList<T>)[];
        var curr = current  ?? (IReadOnlyList<T>)[];
        var prevMap = prev.ToDictionary(keyOf, StringComparer.Ordinal);
        var currMap = curr.ToDictionary(keyOf, StringComparer.Ordinal);

        foreach (var item in curr)
        {
            var key = keyOf(item);
            if (!prevMap.ContainsKey(key))
                added.Add(wrapPayload(item));
            else if (!JsonEquals(item, prevMap[key]))
                updated.Add(wrapPayload(item));
        }

        foreach (var key in prev.Select(keyOf).Where(k => !currMap.ContainsKey(k)))
            removed.Add(wrapRemoval(key));
    }

    private static bool JsonEquals<T>(T? a, T? b) =>
        JsonSerializer.Serialize(a, JsonOptions) ==
        JsonSerializer.Serialize(b, JsonOptions);

    // ── Token ─────────────────────────────────────────────────────────────────

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
        [JsonPropertyName("SwVersion")]  public string?                        RevisionNumber          { get; init; }
        [JsonPropertyName("activeDoc")]  public SwDocumentState?               ActiveDocument          { get; init; }
        [JsonPropertyName("docTokens")] public string?                         DocumentModelTokens     { get; init; }
        [JsonPropertyName("selection")] public IReadOnlyList<SwSelectionState> Selection               { get; init; } = [];
        [JsonPropertyName("activeConfig")] public string?                      ActiveConfigurationName { get; init; }
    }
}

// ── Full-response records ─────────────────────────────────────────────────────

public sealed record SwFullSnapshot
{
    [JsonPropertyName("mode")]     public string           Mode          { get; init; } = "full";
    [JsonPropertyName("snapshot")] public string           SnapshotToken { get; init; } = string.Empty;
    [JsonPropertyName("SwVersion")]public string?          SwVersion     { get; init; }
    [JsonPropertyName("state")]    public SwSessionContext State         { get; init; } = new();
}

public sealed record SwSessionContext
{
    [JsonPropertyName("activeDoc")]    public SwDocumentState?                ActiveDocument          { get; init; }
    [JsonPropertyName("selection")]    public IReadOnlyList<SwSelectionState>  Selection              { get; init; } = [];
    [JsonPropertyName("activeConfig")] public string?                         ActiveConfigurationName { get; init; }
    [JsonPropertyName("documents")]    public IReadOnlyList<SwDocumentDetail>  Documents              { get; init; } = [];
}

// ── Patch records ─────────────────────────────────────────────────────────────

/// <summary>
/// Patch response envelope. Only mode/prevSnap/snapshot are at root;
/// all actual changes are nested under "state".
/// </summary>
public sealed record SwStatePatch
{
    [JsonPropertyName("mode")]     public string       Mode                  { get; init; } = "patch";
    [JsonPropertyName("prevSnap")] public string?      PreviousSnapshotToken { get; init; }
    [JsonPropertyName("snapshot")] public string       SnapshotToken         { get; init; } = string.Empty;
    [JsonPropertyName("state")]    public SwPatchState? State                { get; init; }

    [JsonIgnore]
    public bool IsEmpty => State is null;
}

/// <summary>
/// The changes payload nested under "state" in a patch response.
/// </summary>
public sealed record SwPatchState
{
    /// <summary>Newly added items: documents, mates, features, or components.</summary>
    [JsonPropertyName("added")]   public IReadOnlyList<SwChangeItem>? Added   { get; init; }
    /// <summary>Items whose content changed, including session-level fields (activeDoc, selection, activeConfig).</summary>
    [JsonPropertyName("updated")] public IReadOnlyList<SwChangeItem>? Updated { get; init; }
    /// <summary>Removed items (identity only).</summary>
    [JsonPropertyName("removed")] public IReadOnlyList<SwChangeItem>? Removed { get; init; }
}

/// <summary>
/// A single change entry in a patch's added/updated/removed list.
/// Exactly one payload field is populated per entry; all others are null and omitted from JSON.
///
/// Session updated:   one of <see cref="ActiveDocument"/>, <see cref="ActiveConfigurationName"/>, <see cref="Selection"/>.
/// Document added:    <see cref="Document"/> is set.
/// Document removed:  <see cref="DocumentId"/> is set.
/// Sub-doc added/updated: <see cref="DocId"/> + one of <see cref="Mate"/>, <see cref="Feature"/>, <see cref="Component"/>.
/// Sub-doc removed:   <see cref="DocId"/> + one of <see cref="MateName"/>, <see cref="FeatureName"/>, <see cref="ComponentName"/>.
/// Geometry changed:  <see cref="DocId"/> + <see cref="DocSnapshot"/> + any changed scalar(s).
/// </summary>
public sealed record SwChangeItem
{
    // Session-level (appear in state.updated)
    [JsonPropertyName("activeDoc")]    public SwDocumentState?                 ActiveDocument          { get; init; }
    [JsonPropertyName("activeConfig")] public string?                          ActiveConfigurationName { get; init; }
    [JsonPropertyName("selection")]    public IReadOnlyList<SwSelectionState>?  Selection              { get; init; }

    // Document-level
    [JsonPropertyName("document")]   public SwDocumentDetail? Document   { get; init; }
    [JsonPropertyName("documentId")] public string?           DocumentId { get; init; }

    // Sub-document scope
    [JsonPropertyName("docId")]      public string?  DocId       { get; init; }
    [JsonPropertyName("docSnapshot")]public string?  DocSnapshot { get; init; }

    // Sub-document content (added / updated)
    [JsonPropertyName("mate")]      public MateInfo?      Mate      { get; init; }
    [JsonPropertyName("feature")]   public FeatureInfo?   Feature   { get; init; }
    [JsonPropertyName("component")] public ComponentInfo? Component { get; init; }

    // Sub-document identity (removed)
    [JsonPropertyName("mateName")]      public string? MateName      { get; init; }
    [JsonPropertyName("featureName")]   public string? FeatureName   { get; init; }
    [JsonPropertyName("componentName")] public string? ComponentName { get; init; }

    // Geometry scalars (included in "updated" when only geometry changed)
    [JsonPropertyName("boundsMm")]  public double[]? BoundsMm  { get; init; }
    [JsonPropertyName("massG")]     public double?   MassGrams { get; init; }
    [JsonPropertyName("volumeMm3")] public double?   VolumeMm3 { get; init; }
    [JsonPropertyName("unsaved")]   public bool?     Unsaved   { get; init; }
}

// ── Data records ──────────────────────────────────────────────────────────────

/// <summary>Compact document reference used for the activeDoc field (id, title, path, type, config only).</summary>
public sealed record SwDocumentState(
    [property: JsonPropertyName("id")]     string  Id,
    [property: JsonPropertyName("title")]  string? Title,
    [property: JsonPropertyName("path")]   string? Path,
    [property: JsonPropertyName("type")]   string  DocumentType,
    [property: JsonPropertyName("config")] string? ConfigurationName);

/// <summary>
/// Full document entry in the documents array: session metadata + complete model data.
/// Parts have <see cref="Features"/>; assemblies have <see cref="Components"/> and <see cref="Mates"/>.
/// </summary>
public sealed record SwDocumentDetail
{
    [JsonPropertyName("id")]       public string  Id               { get; init; } = string.Empty;
    [JsonPropertyName("title")]    public string? Title            { get; init; }
    [JsonPropertyName("path")]     public string? Path             { get; init; }
    [JsonPropertyName("type")]     public string  DocumentType     { get; init; } = string.Empty;
    [JsonPropertyName("config")]   public string? ConfigurationName { get; init; }
    [JsonPropertyName("snapshot")] public string  ModelSnapshot    { get; init; } = string.Empty;
    [JsonPropertyName("unsaved")]  public bool    Unsaved          { get; init; }
    [JsonPropertyName("boundsMm")] public double[]? BoundsMm       { get; init; }
    [JsonPropertyName("massG")]    public double?   MassGrams      { get; init; }
    [JsonPropertyName("volumeMm3")]public double?   VolumeMm3      { get; init; }
    [JsonPropertyName("features")]   public IReadOnlyList<FeatureInfo>?   Features   { get; init; }
    [JsonPropertyName("components")] public IReadOnlyList<ComponentInfo>? Components { get; init; }
    [JsonPropertyName("mates")]      public IReadOnlyList<MateInfo>?      Mates      { get; init; }
}

/// <summary>One selected entity in the active document.</summary>
public sealed record SwSelectionState(
    [property: JsonPropertyName("key")]        string  Key,
    [property: JsonPropertyName("documentId")] string  DocumentId,
    [property: JsonPropertyName("type")]       string  SelectionType,
    [property: JsonPropertyName("name")]       string? Name,
    [property: JsonPropertyName("mark")]       int?    Mark);
