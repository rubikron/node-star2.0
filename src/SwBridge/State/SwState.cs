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
    /// Patch response: generic JSON diff of the "state" object between two full snapshots.
    /// Top-level keys of "state" are classified as added, updated, or removed.
    /// The new value is included for added/updated; the old value is included for removed.
    /// </summary>
    public SwStatePatch BuildPatch(SwState? previous)
    {
        previous ??= Create(null, null, null, null, null);

        var prevNode = JsonSerializer.SerializeToNode(ToSessionContext(previous), JsonOptions)?.AsObject();
        var currNode = JsonSerializer.SerializeToNode(ToSessionContext(this),     JsonOptions)?.AsObject();

        var added   = new JsonObject();
        var updated = new JsonObject();
        var removed = new JsonObject();

        foreach (var (key, currVal) in currNode ?? [])
        {
            if (prevNode is null || !prevNode.ContainsKey(key))
                added[key] = currVal?.DeepClone();
            else if (!JsonNodeEquals(prevNode[key], currVal))
                updated[key] = currVal?.DeepClone();
        }

        foreach (var (key, prevVal) in prevNode ?? [])
        {
            if (currNode is null || !currNode.ContainsKey(key))
                removed[key] = prevVal?.DeepClone();
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
            PreviousSnapshotToken = previous.SnapshotToken,
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

    private static SwSessionContext ToSessionContext(SwState s) => new()
    {
        ActiveDocument          = s.ActiveDocument,
        Selection               = s.Selection,
        ActiveConfigurationName = s.ActiveConfigurationName,
        Documents               = s.OpenDocuments
    };

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b) =>
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
/// Each bucket is a JSON object whose keys are top-level "state" keys and whose values are
/// the new value (added/updated) or the old value (removed).
/// </summary>
public sealed record SwPatchState
{
    /// <summary>Keys that did not exist in the previous state.</summary>
    [JsonPropertyName("added")]   public JsonObject? Added   { get; init; }
    /// <summary>Keys that existed in both states but whose value changed (new value included).</summary>
    [JsonPropertyName("updated")] public JsonObject? Updated { get; init; }
    /// <summary>Keys that existed in the previous state but are absent now (old value included).</summary>
    [JsonPropertyName("removed")] public JsonObject? Removed { get; init; }
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
