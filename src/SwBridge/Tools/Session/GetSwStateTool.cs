using SwBridge.Connection;
using SwBridge.Models;

namespace SwBridge.Tools.Session;

/// <summary>
/// Returns the current SOLIDWORKS session context as compact JSON for LLM consumption.
/// Supports either a full snapshot or a field-level patch against the previous snapshot.
/// </summary>
public sealed class GetSwStateTool : ISwTool
{
    private readonly ISwConnector _connector;
    private readonly SwStateCollector _collector;
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
                var current = _collector.Collect(_connector);

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

    private SwState? ResolvePreviousState(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("previous_state", out var serializedState))
        {
            return SwState.FromJson(serializedState) ?? _lastSnapshot;
        }

        return _lastSnapshot;
    }
}
