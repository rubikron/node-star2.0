using SwBridge.Connection;
using SwBridge.Models;

namespace SwBridge.Tools.Model;

/// <summary>
/// Returns a human-readable snapshot of the active SOLIDWORKS document for LLM context.
/// Delegates all COM collection to <see cref="ModelStateCollector"/> and all
/// formatting to <see cref="ModelState.ToText"/>.
/// </summary>
public sealed class GetModelStateTool : ISwTool
{
    private readonly ISwConnector        _connector;
    private readonly ModelStateCollector _collector;

    /// <param name="connector">Connected SOLIDWORKS connector.</param>
    public GetModelStateTool(ISwConnector connector)
    {
        _connector = connector;
        _collector = new ModelStateCollector();
    }

    /// <inheritdoc/>
    public string Name => "get_model_state";

    /// <inheritdoc/>
    public string Description =>
        "Returns a structured summary of the active SOLIDWORKS document. " +
        "For parts: feature tree with dimension values, sketch geometry (plane, normal, origin, constraints), " +
        "extrude parameters (depth, direction, end condition), bounding box, and mass properties. " +
        "For assemblies: component tree, mates with resolved face/entity names, bounding box, and mass. " +
        "No parameters required. " +
        "Call this before writing any revision or inspection macro so you have accurate feature names, " +
        "dimension values, and structural context.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (_connector.State != SwConnectionState.Ready || _connector.Application is null)
            return Task.FromResult(
                $"ERROR: SOLIDWORKS is not connected. Current state: {_connector.State}.");

        return Task.Run(
            () => _collector.Collect(_connector.Application).ToText(),
            cancellationToken);
    }
}
