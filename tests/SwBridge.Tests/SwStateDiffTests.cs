using System.Text.Json;
using SwBridge.Models;
using SwBridge.Tools.Session;

namespace SwBridge.Tests;

/// <summary>
/// Covers the readable SOLIDWORKS state snapshot and diff contract.
/// </summary>
public sealed class SwStateDiffTests
{
    /// <summary>
    /// Verifies that unchanged snapshots generate an explicit empty patch envelope.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenStateIsUnchanged_ReturnsExplicitEmptyPatchEnvelope()
    {
        var state = CreateState();

        var patch = state.BuildPatch(state);

        Assert.True(patch.IsEmpty);
        Assert.Equal("patch", patch.Mode);
        Assert.Equal(state.SnapshotToken, patch.SnapshotToken);
        Assert.Equal(state.SnapshotToken, patch.PreviousSnapshotToken);
    }

    /// <summary>
    /// Verifies that serialized full snapshots use the correct structure:
    /// mode/snapshot/SwVersion at root; session fields inside "state".
    /// </summary>
    [Fact]
    public void ToFullResponseJson_UsesCorrectStructure()
    {
        var state = CreateState();

        using var document = JsonDocument.Parse(state.ToFullResponseJson());
        var root = document.RootElement;
        var stateElement = root.GetProperty("state");

        // Root-level fields
        Assert.Equal("full", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("snapshot", out _));
        Assert.True(root.TryGetProperty("SwVersion", out _));

        // Session fields inside "state"
        Assert.True(stateElement.TryGetProperty("activeDoc", out _));
        Assert.True(stateElement.TryGetProperty("documents", out _));
        Assert.True(stateElement.TryGetProperty("selection", out _));
        Assert.True(stateElement.TryGetProperty("activeConfig", out _));

        // snapshot and SwVersion must NOT be inside "state"
        Assert.False(stateElement.TryGetProperty("snapshot", out _));
        Assert.False(stateElement.TryGetProperty("SwVersion", out _));
    }

    /// <summary>
    /// Verifies that switching the active document only changes the expected session fields.
    /// Document collection patch is empty when only session-level fields (activeDoc, config) change.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveDocumentChanges_UpdatesSessionFieldsOnly()
    {
        var docA = MakeDetail("c:\\parts\\a.sldprt", "A", "prt", "Default", modelSnapshot: "tok-a");
        var docB = MakeDetail("c:\\parts\\b.sldprt", "B", "prt", "Default", modelSnapshot: "tok-b");

        var previous = CreateState(
            activeDocument: ToRef(docA),
            openDocuments:  [docA, docB],
            selection:
            [
                new SwSelectionState("c:\\parts\\a.sldprt|face|Face1", "c:\\parts\\a.sldprt", "face", "Face1", null)
            ],
            activeConfiguration: "Default");

        var current = CreateState(
            activeDocument: ToRef(docB),
            openDocuments:  [docA, docB],   // same model content, same snapshots
            selection:
            [
                new SwSelectionState("c:\\parts\\b.sldprt|edge|Edge1", "c:\\parts\\b.sldprt", "edge", "Edge1", null)
            ],
            activeConfiguration: "Default");

        var patch = current.BuildPatch(previous);

        Assert.Equal(current.ActiveDocument, patch.ActiveDocument);
        Assert.NotNull(patch.Selection);
        Assert.Null(patch.OpenDocuments);           // no model content changed
        Assert.Null(patch.ActiveConfigurationName); // config unchanged
    }

    /// <summary>
    /// Verifies add, remove, and update behavior for the open-document diff.
    /// Updates are detected by ModelSnapshot token change.
    /// </summary>
    [Fact]
    public void DocumentCollectionPatch_WhenDocumentsChange_TracksAddedRemovedAndUpdated()
    {
        var previous = new SwDocumentDetail[]
        {
            MakeDetail("a", "A", "prt", "Default", modelSnapshot: "snap-a-v1"),
            MakeDetail("b", "B", "asm", "Default", modelSnapshot: "snap-b")
        };

        var current = new SwDocumentDetail[]
        {
            MakeDetail("a", "A", "prt", "Default", modelSnapshot: "snap-a-v2"), // same id, new model
            MakeDetail("c", "C", "drw", null,      modelSnapshot: "snap-c")     // new doc
            // "b" removed
        };

        var patch = SwDocumentCollectionPatch.Create(previous, current);
        var added   = Assert.IsAssignableFrom<IReadOnlyList<SwDocumentDetail>>(patch.Added);
        var updated = Assert.IsAssignableFrom<IReadOnlyList<SwDocumentDetail>>(patch.Updated);
        var removed = Assert.IsAssignableFrom<IReadOnlyList<string>>(patch.Removed);

        Assert.Single(added);
        Assert.Equal("c", added[0].Id);
        Assert.Single(updated);
        Assert.Equal("a", updated[0].Id);
        Assert.Single(removed);
        Assert.Equal("b", removed[0]);
    }

    /// <summary>
    /// Verifies that serialized patch payloads use the readable field names.
    /// </summary>
    [Fact]
    public void ToPatchResponseJson_UsesReadableFieldNames()
    {
        var previous = CreateState(activeConfiguration: "Default");
        var current  = CreateState(activeConfiguration: "Alt");

        using var document = JsonDocument.Parse(current.ToPatchResponseJson(previous));
        var root = document.RootElement;

        Assert.Equal("patch", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("prevSnap", out _));
        Assert.True(root.TryGetProperty("snapshot", out _));
        Assert.True(root.TryGetProperty("activeConfig", out _));
    }

    /// <summary>
    /// Verifies that selection changes replace only the selection payload.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenSelectionChanges_OnlySelectionPayloadChanges()
    {
        var previous = CreateState(
            selection:
            [
                new SwSelectionState("doc|face|Face1", "doc", "face", "Face1", 1)
            ]);

        var current = CreateState(
            selection:
            [
                new SwSelectionState("doc|edge|Edge1", "doc", "edge", "Edge1", 1)
            ]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.Selection);
        Assert.Single(patch.Selection!);
        Assert.Null(patch.ActiveDocument);
        Assert.Null(patch.OpenDocuments);
        Assert.Null(patch.ActiveConfigurationName);
    }

    /// <summary>
    /// Verifies that unsaved document identities remain stable for a session token.
    /// </summary>
    [Fact]
    public void BuildDocumentIdentity_ForUnsavedDocument_UsesStableFallbackToken()
    {
        const string fallbackToken = "ptr:1ab";

        var first  = SwStateCollector.BuildDocumentIdentity(null,          fallbackToken);
        var second = SwStateCollector.BuildDocumentIdentity(string.Empty,  fallbackToken);

        Assert.Equal("unsaved:ptr:1ab", first);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Verifies that full response payloads can be parsed back into snapshots.
    /// </summary>
    [Fact]
    public void FromJson_WhenGivenFullResponseEnvelope_ReturnsEmbeddedState()
    {
        var state = CreateState();

        var parsed = SwState.FromJson(state.ToFullResponseJson());

        var actual = Assert.IsType<SwState>(parsed);
        Assert.Equal(state.SnapshotToken, actual.SnapshotToken);
        Assert.Equal(state.RevisionNumber, actual.RevisionNumber);
        Assert.Equal(state.ActiveDocument, actual.ActiveDocument);
        Assert.Equal(state.ActiveConfigurationName, actual.ActiveConfigurationName);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SwDocumentDetail MakeDetail(
        string  id,
        string? title         = null,
        string  type          = "prt",
        string? config        = "Default",
        string  modelSnapshot = "") =>
        new SwDocumentDetail
        {
            Id                = id,
            Title             = title,
            DocumentType      = type,
            ConfigurationName = config,
            ModelSnapshot     = modelSnapshot
        };

    private static SwDocumentState ToRef(SwDocumentDetail d) =>
        new SwDocumentState(d.Id, d.Title, d.Path, d.DocumentType, d.ConfigurationName);

    private static SwState CreateState(
        SwDocumentState?              activeDocument    = null,
        IReadOnlyList<SwDocumentDetail>? openDocuments  = null,
        IReadOnlyList<SwSelectionState>? selection      = null,
        string?                       activeConfiguration = "Default")
    {
        var defaultDetail = MakeDetail(
            "doc", "Part1", "prt", activeConfiguration, modelSnapshot: "snap-default");

        var defaultActive = activeDocument ?? ToRef(defaultDetail);

        return SwState.Create(
            "33.1.0",
            defaultActive,
            openDocuments ?? [defaultDetail],
            selection ?? [],
            activeConfiguration);
    }
}
