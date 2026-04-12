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
    /// No add/update/remove items when only session-level fields (activeDoc, selection) change.
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
        Assert.Null(patch.Added);             // no model content changed
        Assert.Null(patch.Updated);           // no model content changed
        Assert.Null(patch.Removed);           // no model content changed
        Assert.Null(patch.ActiveConfigurationName); // config unchanged
    }

    /// <summary>
    /// Verifies add, remove, and update behavior for the flat change lists.
    /// New documents appear in Added[*].Document; closed docs in Removed[*].DocumentId;
    /// changed sub-items (mates, features, components) appear in Updated[*].
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

        // C was added as a new document
        Assert.NotNull(patch.Added);
        var addedDocs = patch.Added!.Where(i => i.Document is not null).ToList();
        Assert.Single(addedDocs);
        Assert.Equal("c", addedDocs[0].Document!.Id);

        // B was removed
        Assert.NotNull(patch.Removed);
        var removedDocs = patch.Removed!.Where(i => i.DocumentId is not null).ToList();
        Assert.Single(removedDocs);
        Assert.Equal("b", removedDocs[0].DocumentId);

        // The mate in doc A was updated (alignment changed)
        Assert.NotNull(patch.Updated);
        var updatedMates = patch.Updated!.Where(i => i.Mate is not null).ToList();
        Assert.Single(updatedMates);
        Assert.Equal("a", updatedMates[0].DocId);
        Assert.Equal("Coincident1", updatedMates[0].Mate!.Name);
    }

    /// <summary>
    /// Verifies that serialized patch payloads use the readable field names at root level.
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
        Assert.Null(patch.Added);
        Assert.Null(patch.Updated);
        Assert.Null(patch.Removed);
        Assert.Null(patch.ActiveConfigurationName);
    }

    /// <summary>
    /// Verifies that new mates added to a document appear in the flat Added list.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenMateAdded_AppearsInFlatAddedList()
    {
        var mate = new MateInfo("Coincident4", "coincident", "face1", "face2", null, false, "aligned", false);

        var docPrev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v1");
        var docCurr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v2", mates: [mate]);

        var previous = CreateState(openDocuments: [docPrev]);
        var current  = CreateState(openDocuments: [docCurr]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.Added);
        var addedMates = patch.Added!.Where(i => i.Mate is not null).ToList();
        Assert.Single(addedMates);
        Assert.Equal("a",            addedMates[0].DocId);
        Assert.Equal("Coincident4",  addedMates[0].Mate!.Name);
    }

    /// <summary>
    /// Verifies that removed mates appear in the flat Removed list with identity-only items.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenMateRemoved_AppearsInFlatRemovedList()
    {
        var mate = new MateInfo("Coincident2", "coincident", "face1", "face2", null, false, "aligned", false);

        var docPrev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v1", mates: [mate]);
        var docCurr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v2");

        var previous = CreateState(openDocuments: [docPrev]);
        var current  = CreateState(openDocuments: [docCurr]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.Removed);
        var removedMates = patch.Removed!.Where(i => i.MateName is not null).ToList();
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
        SwDocumentState?                 activeDocument    = null,
        IReadOnlyList<SwDocumentDetail>? openDocuments     = null,
        IReadOnlyList<SwSelectionState>? selection         = null,
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
