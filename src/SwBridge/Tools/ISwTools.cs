// src/SwBridge/Tools/ISwTool.cs
namespace SwBridge.Tools;

/// <summary>
/// Defines the contract for a single SOLIDWORKS tool callable by the orchestrator.
/// Tool names should be unique and use snake_case.
/// </summary>
public interface ISwTool
{
    /// <summary>
    /// Gets the tool name exposed to the LLM.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the human-readable tool description sent to the LLM.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the tool and returns a plain-text result for the LLM.
    /// </summary>
    /// <param name="parameters">Arguments matching the tool schema.</param>
    /// <param name="cancellationToken">Token used to cancel long-running work.</param>
    /// <returns>A success string or an <c>ERROR:</c> message.</returns>
    Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default);
}
