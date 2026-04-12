using SolidWorks.Interop.sldworks;
using SwBridge.Connection;
using SwBridge.Models;
using SwBridge.Tools.Model;

namespace SwBridge.Tools.Session;

/// <summary>
/// Returns the current SOLIDWORKS session context as compact JSON for LLM consumption.
/// Supports either a full snapshot or a field-level patch against the previous snapshot.
/// </summary>
public sealed class GetSwStateTool : ISwTool
{
    private readonly ISwConnector _connector;
    private readonly SwStateCollector _collector;
    private readonly ModelStateCollector _modelCollector;
    private readonly object _sync = new();
    private SwState? _lastSnapshot;

    /// <summary>
    /// Initializes the tool with the active SOLIDWORKS connector.
    /// </summary>
    /// <param name="connector">Connected SOLIDWORKS connector.</param>
    /// <param name="collector">State collector used to read live session data.</param>
    public GetSwStateTool(ISwConnector connector, SwStateCollector? collector = null)
    {
        _connector = connector;
        _collector = collector ?? new SwStateCollector();
        _modelCollector = new ModelStateCollector();
    }

    /// <inheritdoc/>
    public string Name => "get_sw_state";

    /// <inheritdoc/>
    public string Description =>
        "Returns the current SOLIDWORKS session state as compact JSON. " +
        "Parameters: " +
        "'mode' = 'full' or 'patch'. " +
        "'previous_state' (optional) = prior full snapshot JSON when requesting a patch. " +
        "Full mode returns the entire current state. " +
        "Patch mode returns only changed fields and changed open-document entries.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (_connector.State != SwConnectionState.Ready || _connector.Application is null)
        {
            return Task.FromResult(
                "ERROR: SOLIDWORKS is not connected. " +
                $"Current state: {_connector.State}.");
        }

        var mode = parameters.TryGetValue("mode", out var requestedMode) &&
                   !string.IsNullOrWhiteSpace(requestedMode)
            ? requestedMode.Trim().ToLowerInvariant()
            : "full";

        if (mode is not ("full" or "patch"))
        {
            return Task.FromResult(
                "ERROR: Invalid 'mode'. Supported values are 'full' and 'patch'.");
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Collect session metadata (open docs with empty model fields)
                var session = _collector.Collect(_connector);

                // Build a COM document map (path → IModelDoc2) for enrichment
                var comDocMap = BuildComDocumentMap(_connector.Application);

                // Enrich each document with full model data
                var enrichedDocs = session.OpenDocuments.Select(doc =>
                {
                    if (!comDocMap.TryGetValue(doc.Id, out var comDoc))
                        return doc;

                    var model = _modelCollector.CollectDocument(comDoc, _connector.Application!);
                    return doc with
                    {
                        ModelSnapshot = model.SnapshotToken,
                        Unsaved       = model.Unsaved,
                        BoundsMm      = model.BoundsMm,
                        MassGrams     = model.MassGrams,
                        VolumeMm3     = model.VolumeMm3,
                        Features      = model.Features.Count   > 0 ? model.Features   : null,
                        Components    = model.Components.Count > 0 ? model.Components : null,
                        Mates         = model.Mates.Count      > 0 ? model.Mates      : null,
                    };
                }).ToList();

                var current = SwState.Create(
                    session.RevisionNumber,
                    session.ActiveDocument,
                    enrichedDocs,
                    session.Selection,
                    session.ActiveConfigurationName);

                lock (_sync)
                {
                    var previous = ResolvePreviousState(parameters);
                    var response = mode == "patch"
                        ? current.ToPatchResponseJson(previous)
                        : current.ToFullResponseJson();

                    _lastSnapshot = current;
                    return response;
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Iterates the COM document chain and maps normalized path → IModelDoc2.
    /// Unsaved documents (no path) are excluded since they cannot be matched by ID.
    /// </summary>
    private static Dictionary<string, IModelDoc2> BuildComDocumentMap(ISldWorks? app)
    {
        var map = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        if (app is null) return map;

        try
        {
            var doc = app.IGetFirstDocument2();
            while (doc is not null)
            {
                try
                {
                    var path = doc.GetPathName();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var id = Path.GetFullPath(path).ToLowerInvariant();
                        map.TryAdd(id, doc);
                    }
                }
                catch { }

                try { doc = doc.IGetNext(); }
                catch { break; }
            }
        }
        catch { }

        return map;
    }

    private SwState? ResolvePreviousState(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("previous_state", out var serializedState))
            return SwState.FromJson(serializedState) ?? _lastSnapshot;

        return _lastSnapshot;
    }
}
