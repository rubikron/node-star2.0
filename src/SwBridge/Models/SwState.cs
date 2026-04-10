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

    /// <summary>
    /// Gets the deterministic token for this snapshot.
    /// </summary>
    [JsonPropertyName("t")]
    public string SnapshotToken { get; init; } = string.Empty;

    /// <summary>
    /// Gets the connected SOLIDWORKS revision number when available.
    /// </summary>
    [JsonPropertyName("rev")]
    public string? RevisionNumber { get; init; }

    /// <summary>
    /// Gets the active document summary, or <see langword="null"/> if no document is active.
    /// </summary>
    [JsonPropertyName("act")]
    public SwDocumentState? ActiveDocument { get; init; }

    /// <summary>
    /// Gets the currently open documents in deterministic order.
    /// </summary>
    [JsonPropertyName("docs")]
    public IReadOnlyList<SwDocumentState> OpenDocuments { get; init; } = [];

    /// <summary>
    /// Gets the current selection summaries in deterministic order.
    /// </summary>
    [JsonPropertyName("sel")]
    public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

    /// <summary>
    /// Gets the active configuration name for the active document when available.
    /// </summary>
    [JsonPropertyName("cfg")]
    public string? ActiveConfigurationName { get; init; }

    /// <summary>
    /// Creates a normalized snapshot with a deterministic token.
    /// </summary>
    /// <param name="revisionNumber">Connected SOLIDWORKS revision number.</param>
    /// <param name="activeDocument">Active document summary.</param>
    /// <param name="openDocuments">Open document summaries.</param>
    /// <param name="selection">Current selection summaries.</param>
    /// <param name="activeConfigurationName">Active configuration name.</param>
    /// <returns>A normalized, tokenized snapshot.</returns>
    public static SwState Create(
        string? revisionNumber,
        SwDocumentState? activeDocument,
        IEnumerable<SwDocumentState>? openDocuments,
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

    /// <summary>
    /// Serializes the snapshot into compact JSON.
    /// </summary>
    /// <returns>Compact JSON representing the snapshot.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Serializes a full-state tool response containing this snapshot.
    /// </summary>
    /// <returns>Compact JSON with the full snapshot payload.</returns>
    public string ToFullResponseJson() =>
        JsonSerializer.Serialize(
            new SwStateFullResponse
            {
                Mode = "full",
                State = this
            },
            JsonOptions);

    /// <summary>
    /// Builds a compact patch from a previous snapshot to the current one.
    /// </summary>
    /// <param name="previous">The previous snapshot, if available.</param>
    /// <returns>A patch payload describing only the changed fields.</returns>
    public SwStatePatch BuildPatch(SwState? previous)
    {
        var previousSnapshotToken = previous?.SnapshotToken;
        previous ??= Create(null, null, null, null, null);

        var patch = new SwStatePatch
        {
            PreviousSnapshotToken = previousSnapshotToken,
            SnapshotToken = SnapshotToken
        };

        if (!string.Equals(previous.RevisionNumber, RevisionNumber, StringComparison.Ordinal))
        {
            patch.RevisionNumber = RevisionNumber;
        }

        if (previous.ActiveDocument != ActiveDocument)
        {
            patch.ActiveDocument = ActiveDocument;
        }

        if (!string.Equals(previous.ActiveConfigurationName, ActiveConfigurationName, StringComparison.Ordinal))
        {
            patch.ActiveConfigurationName = ActiveConfigurationName;
        }

        if (!previous.Selection.SequenceEqual(Selection))
        {
            patch.Selection = Selection;
        }

        var documentPatch = SwDocumentCollectionPatch.Create(previous.OpenDocuments, OpenDocuments);
        if (!documentPatch.IsEmpty)
        {
            patch.OpenDocuments = documentPatch;
        }

        return patch;
    }

    /// <summary>
    /// Serializes a patch response from a previous snapshot to the current one.
    /// </summary>
    /// <param name="previous">The previous snapshot, if available.</param>
    /// <returns>Compact JSON containing only the changed fields.</returns>
    public string ToPatchResponseJson(SwState? previous) =>
        JsonSerializer.Serialize(BuildPatch(previous), JsonOptions);

    /// <summary>
    /// Deserializes a snapshot from either a bare state payload or a full response envelope.
    /// </summary>
    /// <param name="json">Serialized JSON from a prior state tool response.</param>
    /// <returns>The deserialized snapshot, or <see langword="null"/> if parsing fails.</returns>
    public static SwState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root)
            {
                return null;
            }

            if (root["s"] is JsonNode stateNode)
            {
                return stateNode.Deserialize<SwState>(JsonOptions);
            }

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
                RevisionNumber = state.RevisionNumber,
                ActiveDocument = state.ActiveDocument,
                OpenDocuments = state.OpenDocuments,
                Selection = state.Selection,
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
        [JsonPropertyName("rev")]
        public string? RevisionNumber { get; init; }

        [JsonPropertyName("act")]
        public SwDocumentState? ActiveDocument { get; init; }

        [JsonPropertyName("docs")]
        public IReadOnlyList<SwDocumentState> OpenDocuments { get; init; } = [];

        [JsonPropertyName("sel")]
        public IReadOnlyList<SwSelectionState> Selection { get; init; } = [];

        [JsonPropertyName("cfg")]
        public string? ActiveConfigurationName { get; init; }
    }
}

/// <summary>
/// Represents a compact document summary within a session snapshot.
/// </summary>
/// <param name="Id">Stable document identity used for diffs.</param>
/// <param name="Title">Current document title.</param>
/// <param name="Path">Document path when saved to disk.</param>
/// <param name="DocumentType">Compact document type code.</param>
/// <param name="ActiveConfigurationName">Active configuration for the document when available.</param>
public sealed record SwDocumentState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ttl")] string? Title,
    [property: JsonPropertyName("pth")] string? Path,
    [property: JsonPropertyName("typ")] string DocumentType,
    [property: JsonPropertyName("cfg")] string? ActiveConfigurationName);

/// <summary>
/// Represents a compact summary of one selected entity in the active document.
/// </summary>
/// <param name="Key">Stable selection identity used for diffs.</param>
/// <param name="DocumentId">Owning document identity.</param>
/// <param name="SelectionType">Compact selection type code.</param>
/// <param name="Name">Best-effort human-readable selection name.</param>
/// <param name="Mark">Selection mark when available.</param>
public sealed record SwSelectionState(
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("doc")] string DocumentId,
    [property: JsonPropertyName("typ")] string SelectionType,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("m")] int? Mark);

/// <summary>
/// Represents the full-state response envelope returned by the session tool.
/// </summary>
public sealed record SwStateFullResponse
{
    /// <summary>
    /// Gets the response mode.
    /// </summary>
    [JsonPropertyName("m")]
    public string Mode { get; init; } = "full";

    /// <summary>
    /// Gets the current full snapshot.
    /// </summary>
    [JsonPropertyName("s")]
    public SwState State { get; init; } = SwState.Create(null, null, null, null, null);
}

/// <summary>
/// Represents a compact patch response between two snapshots.
/// </summary>
public sealed record SwStatePatch
{
    /// <summary>
    /// Gets the response mode.
    /// </summary>
    [JsonPropertyName("m")]
    public string Mode { get; init; } = "patch";

    /// <summary>
    /// Gets the previous snapshot token when one was available.
    /// </summary>
    [JsonPropertyName("p")]
    public string? PreviousSnapshotToken { get; set; }

    /// <summary>
    /// Gets the current snapshot token.
    /// </summary>
    [JsonPropertyName("t")]
    public string SnapshotToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets the updated SOLIDWORKS revision number when it changed.
    /// </summary>
    [JsonPropertyName("rev")]
    public string? RevisionNumber { get; set; }

    /// <summary>
    /// Gets the updated active document when it changed.
    /// </summary>
    [JsonPropertyName("act")]
    public SwDocumentState? ActiveDocument { get; set; }

    /// <summary>
    /// Gets document collection changes when the open-document set changed.
    /// </summary>
    [JsonPropertyName("docs")]
    public SwDocumentCollectionPatch? OpenDocuments { get; set; }

    /// <summary>
    /// Gets the updated selection list when the selection changed.
    /// </summary>
    [JsonPropertyName("sel")]
    public IReadOnlyList<SwSelectionState>? Selection { get; set; }

    /// <summary>
    /// Gets the updated active configuration name when it changed.
    /// </summary>
    [JsonPropertyName("cfg")]
    public string? ActiveConfigurationName { get; set; }

    /// <summary>
    /// Gets whether the patch carries any actual field changes.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        RevisionNumber is null &&
        ActiveDocument is null &&
        OpenDocuments is null &&
        Selection is null &&
        ActiveConfigurationName is null;
}

/// <summary>
/// Represents changes to the open-document collection.
/// </summary>
public sealed record SwDocumentCollectionPatch
{
    /// <summary>
    /// Gets newly opened documents.
    /// </summary>
    [JsonPropertyName("add")]
    public IReadOnlyList<SwDocumentState>? Added { get; init; }

    /// <summary>
    /// Gets updated documents whose tracked fields changed.
    /// </summary>
    [JsonPropertyName("upd")]
    public IReadOnlyList<SwDocumentState>? Updated { get; init; }

    /// <summary>
    /// Gets removed document identities.
    /// </summary>
    [JsonPropertyName("rem")]
    public IReadOnlyList<string>? Removed { get; init; }

    /// <summary>
    /// Gets whether the collection patch is empty.
    /// </summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Added is null &&
        Updated is null &&
        Removed is null;

    /// <summary>
    /// Computes a collection patch between previous and current document sets.
    /// </summary>
    /// <param name="previous">Previous document set.</param>
    /// <param name="current">Current document set.</param>
    /// <returns>A patch describing only changed entries.</returns>
    public static SwDocumentCollectionPatch Create(
        IReadOnlyList<SwDocumentState> previous,
        IReadOnlyList<SwDocumentState> current)
    {
        var previousMap = previous.ToDictionary(static doc => doc.Id, StringComparer.Ordinal);
        var currentMap = current.ToDictionary(static doc => doc.Id, StringComparer.Ordinal);

        var added = current
            .Where(doc => !previousMap.ContainsKey(doc.Id))
            .ToArray();

        var updated = current
            .Where(doc => previousMap.TryGetValue(doc.Id, out var earlier) && earlier != doc)
            .ToArray();

        var removed = previous
            .Select(static doc => doc.Id)
            .Where(id => !currentMap.ContainsKey(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        return new SwDocumentCollectionPatch
        {
            Added = added.Length == 0 ? null : added,
            Updated = updated.Length == 0 ? null : updated,
            Removed = removed.Length == 0 ? null : removed
        };
    }
}
