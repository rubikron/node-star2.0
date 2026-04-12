using System.Text.Json;
using SwBridge.Models;
using SwBridge.Tools.Session;

namespace SwBridge.Tests;

/// <summary>
/// Covers the readable SOLIDWORKS state snapshot and diff contract.
/// Patch diffs are generic JSON key comparisons on the top-level "state" object.
/// </summary>
public sealed class SwStateDiffTests
{
    /// <summary>
    /// Verifies that unchanged snapshots produce an empty patch (state key absent).
    /// </summary>
    [Fact]
    public void BuildPatch_WhenStateIsUnchanged_ReturnsEmptyPatch()
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
        var stateEl = root.GetProperty("state");

        Assert.Equal("full", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("snapshot", out _));
        Assert.True(root.TryGetProperty("SwVersion", out _));

        Assert.True(stateEl.TryGetProperty("activeDoc", out _));
        Assert.True(stateEl.TryGetProperty("documents", out _));
        Assert.True(stateEl.TryGetProperty("selection", out _));
        Assert.True(stateEl.TryGetProperty("activeConfig", out _));

        // snapshot and SwVersion must NOT bleed into "state"
        Assert.False(stateEl.TryGetProperty("snapshot", out _));
        Assert.False(stateEl.TryGetProperty("SwVersion", out _));
    }

    /// <summary>
    /// Verifies that a changed activeDoc key appears in state.updated with the new value.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveDocChanges_KeyAppearsInStateUpdated()
    {
        var docA = MakeDetail("c:\\parts\\a.sldprt", "A", "prt", "Default", modelSnapshot: "tok-a");
        var docB = MakeDetail("c:\\parts\\b.sldprt", "B", "prt", "Default", modelSnapshot: "tok-b");

        var previous = CreateState(activeDocument: ToRef(docA), openDocuments: [docA, docB]);
        var current  = CreateState(activeDocument: ToRef(docB), openDocuments: [docA, docB]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Updated);
        Assert.True(patch.State.Updated!.ContainsKey("activeDoc"));
        Assert.Equal(
            current.ActiveDocument!.Id,
            patch.State.Updated["activeDoc"]!["id"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a changed activeConfig key appears in state.updated with the new value.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveConfigChanges_KeyAppearsInStateUpdated()
    {
        var previous = CreateState(activeConfiguration: "Default");
        var current  = CreateState(activeConfiguration: "Alt");

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Updated);
        Assert.True(patch.State.Updated!.ContainsKey("activeConfig"));
        Assert.Equal("Alt", patch.State.Updated["activeConfig"]!.GetValue<string>());
    }

    /// <summary>
    /// Verifies that a removed activeConfig key (null → omitted) appears in state.removed.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenActiveConfigRemoved_KeyAppearsInStateRemoved()
    {
        var previous = CreateState(activeConfiguration: "Default");
        var current  = CreateState(activeConfiguration: null);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Removed);
        Assert.True(patch.State.Removed!.ContainsKey("activeConfig"));
    }

    /// <summary>
    /// Verifies that a changed selection key appears in state.updated with the new array.
    /// </summary>
    [Fact]
    public void BuildPatch_WhenSelectionChanges_KeyAppearsInStateUpdated()
    {
        var previous = CreateState(selection:
        [
            new SwSelectionState("doc|face|Face1", "doc", "face", "Face1", 1)
        ]);
        var current = CreateState(selection:
        [
            new SwSelectionState("doc|edge|Edge1", "doc", "edge", "Edge1", 1)
        ]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Updated);
        Assert.True(patch.State.Updated!.ContainsKey("selection"));
    }

    /// <summary>
    /// Verifies that a changed documents array appears in state.updated.
    /// The new full documents array is the value (not a granular sub-diff).
    /// </summary>
    [Fact]
    public void BuildPatch_WhenDocumentsChange_ArrayAppearsInStateUpdated()
    {
        var mate = new MateInfo("Coincident4", "coincident", "face1", "face2", null, false, "aligned", false);

        var docPrev = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v1");
        var docCurr = MakeDetail("a", "A", "asm", "Default", modelSnapshot: "snap-v2", mates: [mate]);

        var previous = CreateState(openDocuments: [docPrev]);
        var current  = CreateState(openDocuments: [docCurr]);

        var patch = current.BuildPatch(previous);

        Assert.NotNull(patch.State);
        Assert.NotNull(patch.State!.Updated);
        Assert.True(patch.State.Updated!.ContainsKey("documents"));

        // Patch contains no other buckets (activeDoc/selection/config unchanged)
        Assert.Null(patch.State.Added);
        Assert.Null(patch.State.Removed);
    }

    /// <summary>
    /// Verifies that the serialized patch has mode/prevSnap/snapshot at root
    /// and all changes nested under "state".
    /// </summary>
    [Fact]
    public void ToPatchResponseJson_ChangesAreNestedUnderState()
    {
        var previous = CreateState(activeConfiguration: "Default");
        var current  = CreateState(activeConfiguration: "Alt");

        using var document = JsonDocument.Parse(current.ToPatchResponseJson(previous));
        var root = document.RootElement;

        // Envelope fields at root
        Assert.Equal("patch", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("prevSnap", out _));
        Assert.True(root.TryGetProperty("snapshot", out _));

        // Changes are under "state", not promoted to root
        Assert.True(root.TryGetProperty("state", out _));
        Assert.False(root.TryGetProperty("activeConfig", out _));
        Assert.False(root.TryGetProperty("activeDoc", out _));
        Assert.False(root.TryGetProperty("documents", out _));
    }

    /// <summary>
    /// Verifies that unsaved document identities remain stable for a session token.
    /// </summary>
    [Fact]
    public void BuildDocumentIdentity_ForUnsavedDocument_UsesStableFallbackToken()
    {
        const string fallbackToken = "ptr:1ab";

        var first  = SwStateCollector.BuildDocumentIdentity(null,         fallbackToken);
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
