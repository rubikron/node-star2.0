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
    /// Verifies that unchanged snapshots generate an explicit empty patch envelope
    /// (state key absent, IsEmpty true).
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
    /// Verifies that switching the active document produces a state.updated entry
    /// containing the new activeDoc value, not a root-level field.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveDocumentChanges_ActiveDocAppearsInStateUpdated()
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

        Assert.NotNull(patch.State);
        var updatedItems = patch.State!.Updated;
        Assert.NotNull(updatedItems);

        // activeDoc change appears as an updated item
        var activeDocUpdate = updatedItems!.SingleOrDefault(i => i.ActiveDocument is not null);
        Assert.NotNull(activeDocUpdate);
        Assert.Equal(current.ActiveDocument, activeDocUpdate!.ActiveDocument);

        // selection change also appears as an updated item
        Assert.Contains(updatedItems!, i => i.Selection is not null);

        // No document model changes
        Assert.Null(patch.State.Added);
        Assert.Null(patch.State.Removed);
        // No activeConfig change
        Assert.DoesNotContain(updatedItems!, i => i.ActiveConfigurationName is not null);
    }

    /// <summary>
    /// Verifies add, remove, and update behavior for the flat state change lists.
    /// New documents appear in state.added[*].document; closed docs in state.removed[*].documentId;
    /// changed sub-items (mates) appear in state.updated[*].
    /// </summary>
    [Fact]
    public void DocumentCollectionPatch_WhenDocumentsChange_TracksAddedRemovedAndUpdated()
    {
        var mateV1 = new MateInfo("Coincident1", "coincident", "face1", "face2", null, false, "aligned", false);
        var mateV2 = new MateInfo("Coincident1", "coincident", "face1", "face2", null, false, "anti-aligned", false); // alignment changed

        var docA_prev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-a-v1", mates: [mateV1]);
        var docA_curr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-a-v2", mates: [mateV2]);
        var docB      = MakeDetail("b", "B", "asm", "Default", modelSnapshot: "snap-b");
        var docC      = MakeDetail("c", "C", "drw", null,      modelSnapshot: "snap-c");

        var previous = CreateState(openDocuments: [docA_prev, docB]);
        var current  = CreateState(openDocuments: [docA_curr, docC]); // B removed, C added, A's mate updated

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);

        // C was added as a new document
        Assert.NotNull(patch.State!.Added);
        var addedDocs = patch.State.Added!.Where(i => i.Document is not null).ToList();
        Assert.Single(addedDocs);
        Assert.Equal("c", addedDocs[0].Document!.Id);

        // B was removed
        Assert.NotNull(patch.State.Removed);
        var removedDocs = patch.State.Removed!.Where(i => i.DocumentId is not null).ToList();
        Assert.Single(removedDocs);
        Assert.Equal("b", removedDocs[0].DocumentId);

        // The mate in doc A was updated (alignment changed)
        Assert.NotNull(patch.State.Updated);
        var updatedMates = patch.State.Updated!.Where(i => i.Mate is not null).ToList();
        Assert.Single(updatedMates);
        Assert.Equal("a",            updatedMates[0].DocId);
        Assert.Equal("Coincident1",  updatedMates[0].Mate!.Name);
    }

    /// <summary>
    /// Verifies that serialized patch payloads use the correct structure:
    /// mode/prevSnap/snapshot at root; changes nested under "state".
    /// </summary>
    [Fact]
    public void ToPatchResponseJson_UsesCorrectStructure()
    {
        var previous = CreateState(activeConfiguration: "Default");
        var current  = CreateState(activeConfiguration: "Alt");

        using var document = JsonDocument.Parse(current.ToPatchResponseJson(previous));
        var root = document.RootElement;

        // Root-level envelope fields
        Assert.Equal("patch", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("prevSnap", out _));
        Assert.True(root.TryGetProperty("snapshot", out _));

        // Changes are under "state", not at root
        Assert.True(root.TryGetProperty("state", out var stateEl));
        Assert.False(root.TryGetProperty("activeConfig", out _));

        // activeConfig change appears inside state.updated
        Assert.True(stateEl.TryGetProperty("updated", out var updatedEl));
        var configUpdate = updatedEl.EnumerateArray()
            .FirstOrDefault(el => el.TryGetProperty("activeConfig", out _));
        Assert.NotEqual(default, configUpdate);
    }

    /// <summary>
    /// Verifies that selection changes appear as an updated item inside state.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenSelectionChanges_SelectionAppearsInStateUpdated()
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

        Assert.NotNull(patch.State);
        var updatedItems = patch.State!.Updated;
        Assert.NotNull(updatedItems);

        var selectionUpdate = updatedItems!.SingleOrDefault(i => i.Selection is not null);
        Assert.NotNull(selectionUpdate);
        Assert.Single(selectionUpdate!.Selection!);

        // Nothing else changed
        Assert.Null(patch.State.Added);
        Assert.Null(patch.State.Removed);
        Assert.DoesNotContain(updatedItems!, i => i.ActiveDocument is not null);
        Assert.DoesNotContain(updatedItems!, i => i.ActiveConfigurationName is not null);
    }

    /// <summary>
    /// Verifies that new mates added to a document appear in state.added.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenMateAdded_AppearsInStateAdded()
    {
        var mate = new MateInfo("Coincident4", "coincident", "face1", "face2", null, false, "aligned", false);

        var docPrev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v1");
        var docCurr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v2", mates: [mate]);

        var previous = CreateState(openDocuments: [docPrev]);
        var current  = CreateState(openDocuments: [docCurr]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Added);
        var addedMates = patch.State.Added!.Where(i => i.Mate is not null).ToList();
        Assert.Single(addedMates);
        Assert.Equal("a",           addedMates[0].DocId);
        Assert.Equal("Coincident4", addedMates[0].Mate!.Name);
    }

    /// <summary>
    /// Verifies that removed mates appear in state.removed with identity-only items.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenMateRemoved_AppearsInStateRemoved()
    {
        var mate = new MateInfo("Coincident2", "coincident", "face1", "face2", null, false, "aligned", false);

        var docPrev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v1", mates: [mate]);
        var docCurr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v2");

        var previous = CreateState(openDocuments: [docPrev]);
        var current  = CreateState(openDocuments: [docCurr]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Removed);
        var removedMates = patch.State.Removed!.Where(i => i.MateName is not null).ToList();
        Assert.Single(removedMates);
        Assert.Equal("a",            removedMates[0].DocId);
        Assert.Equal("Coincident2",  removedMates[0].MateName);
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
        string  modelSnapshot = "",
        IReadOnlyList<FeatureInfo>?   features   = null,
        IReadOnlyList<ComponentInfo>? components = null,
        IReadOnlyList<MateInfo>?      mates      = null) =>
        new SwDocumentDetail
        {
            Id                = id,
            Title             = title,
            DocumentType      = type,
            ConfigurationName = config,
            ModelSnapshot     = modelSnapshot,
            Features          = features,
            Components        = components,
            Mates             = mates
        };

    private static SwDocumentState ToRef(SwDocumentDetail d) =>
        new SwDocumentState(d.Id, d.Title, d.Path, d.DocumentType, d.ConfigurationName);

    private static SwState CreateState(
        SwDocumentState?                 activeDocument      = null,
        IReadOnlyList<SwDocumentDetail>? openDocuments       = null,
        IReadOnlyList<SwSelectionState>? selection           = null,
        string?                          activeConfiguration = "Default")
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
