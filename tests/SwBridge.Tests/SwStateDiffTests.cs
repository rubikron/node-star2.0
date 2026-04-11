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
    /// Verifies that serialized full snapshots use the readable field names.
    /// </summary>
    [Fact]
    public void ToFullResponseJson_UsesReadableFieldNames()
    {
        var state = CreateState();

        using var document = JsonDocument.Parse(state.ToFullResponseJson());
        var root = document.RootElement;
        var stateElement = root.GetProperty("state");

        Assert.Equal("full", root.GetProperty("mode").GetString());
        Assert.True(stateElement.TryGetProperty("snapshot", out _));
        Assert.True(stateElement.TryGetProperty("SwVersion", out _));
        Assert.True(stateElement.TryGetProperty("activeDoc", out _));
        Assert.True(stateElement.TryGetProperty("documents", out _));
        Assert.True(stateElement.TryGetProperty("selection", out _));
        Assert.True(stateElement.TryGetProperty("activeConfig", out _));
    }

    /// <summary>
    /// Verifies that switching the active document only changes the expected fields.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveDocumentChanges_UpdatesActiveDocumentAndDocumentPatch()
    {
        var previous = CreateState(
            activeDocument: new SwDocumentState("c:\\parts\\a.sldprt", "A", "C:\\parts\\a.sldprt", "prt", "Default"),
            openDocuments:
            [
                new SwDocumentState("c:\\parts\\a.sldprt", "A", "C:\\parts\\a.sldprt", "prt", "Default"),
                new SwDocumentState("c:\\parts\\b.sldprt", "B", "C:\\parts\\b.sldprt", "prt", "Default")
            ],
            selection:
            [
                new SwSelectionState("c:\\parts\\a.sldprt|face|Face1", "c:\\parts\\a.sldprt", "face", "Face1", null)
            ],
            activeConfiguration: "Default");

        var current = CreateState(
            activeDocument: new SwDocumentState("c:\\parts\\b.sldprt", "B", "C:\\parts\\b.sldprt", "prt", "Alt"),
            openDocuments:
            [
                new SwDocumentState("c:\\parts\\a.sldprt", "A", "C:\\parts\\a.sldprt", "prt", "Default"),
                new SwDocumentState("c:\\parts\\b.sldprt", "B", "C:\\parts\\b.sldprt", "prt", "Alt")
            ],
            selection:
            [
                new SwSelectionState("c:\\parts\\b.sldprt|edge|Edge1", "c:\\parts\\b.sldprt", "edge", "Edge1", null)
            ],
            activeConfiguration: "Alt");

        var patch = current.BuildPatch(previous);
        var documentsPatch = Assert.IsType<SwDocumentCollectionPatch>(patch.OpenDocuments);

        Assert.Equal(current.ActiveDocument, patch.ActiveDocument);
        Assert.Equal("Alt", patch.ActiveConfigurationName);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SwDocumentState>>(documentsPatch.Updated));
        Assert.Null(documentsPatch.Added);
        Assert.Null(documentsPatch.Removed);
    }

    /// <summary>
    /// Verifies add, remove, and update behavior for the open-document diff.
    /// </summary>
    [Fact]
    public void DocumentCollectionPatch_WhenDocumentsChange_TracksAddedRemovedAndUpdated()
    {
        var previous = new[]
        {
            new SwDocumentState("a", "A", "C:\\a.sldprt", "prt", "Default"),
            new SwDocumentState("b", "B", "C:\\b.sldasm", "asm", "Default")
        };

        var current = new[]
        {
            new SwDocumentState("a", "A", "C:\\a.sldprt", "prt", "Alt"),
            new SwDocumentState("c", "C", "C:\\c.slddrw", "drw", null)
        };

        var patch = SwDocumentCollectionPatch.Create(previous, current);
        var added = Assert.IsAssignableFrom<IReadOnlyList<SwDocumentState>>(patch.Added);
        var updated = Assert.IsAssignableFrom<IReadOnlyList<SwDocumentState>>(patch.Updated);
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
        var current = CreateState(activeConfiguration: "Alt");

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

        var first = SwStateCollector.BuildDocumentIdentity(null, fallbackToken);
        var second = SwStateCollector.BuildDocumentIdentity(string.Empty, fallbackToken);

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
        Assert.Equal(state.OpenDocuments, actual.OpenDocuments);
        Assert.Equal(state.Selection, actual.Selection);
    }

    private static SwState CreateState(
        SwDocumentState? activeDocument = null,
        IReadOnlyList<SwDocumentState>? openDocuments = null,
        IReadOnlyList<SwSelectionState>? selection = null,
        string? activeConfiguration = "Default")
    {
        var defaultActive = activeDocument ?? new SwDocumentState(
            "doc",
            "Part1",
            "C:\\parts\\part1.sldprt",
            "prt",
            activeConfiguration);

        return SwState.Create(
            "33.1.0",
            defaultActive,
            openDocuments ?? [defaultActive],
            selection ?? [],
            activeConfiguration);
    }
}
