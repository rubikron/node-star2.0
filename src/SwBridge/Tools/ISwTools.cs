// src/SwBridge/Tools/ISwTool.cs
namespace SwBridge.Tools;

/// <summary>
/// Defines the contract for a single discrete operation exposed to the
/// LLM orchestrator as a callable tool. Each implementation wraps one
/// logical action — either a SOLIDWORKS session operation or a macro
/// lifecycle action (write, run, list, delete).
///
/// Tool names must be unique across the registry and should follow
/// snake_case convention to match LLM tool-use schema expectations
/// (e.g. "run_macro", "get_sw_state").
/// </summary>
public interface ISwTool
{
    /// <summary>
    /// Gets the unique snake_case name of this tool as it will appear
    /// in the LLM tool schema (e.g. "run_macro", "write_macro").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the human-readable description of what this tool does.
    /// This is passed directly to the LLM as the tool description,
    /// so it should be precise about what parameters are expected
    /// and what the return value means.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the tool with the given parameters and returns a
    /// plain-text result that will be passed back to the LLM as
    /// the tool result. Errors should be returned as descriptive
    /// strings rather than thrown, so the LLM can self-correct.
    /// </summary>
    /// <param name="parameters">
    /// Key-value pairs matching the parameter schema defined in
    /// <see cref="Description"/>. The orchestrator is responsible
    /// for deserializing the LLM's JSON tool call into this dict.
    /// </param>
    /// <param name="cancellationToken">
    /// Token to cancel long-running operations such as macro execution.
    /// </param>
    /// <returns>
    /// A plain-text result string. Return a description of success,
    /// or an error message prefixed with "ERROR:" so the LLM knows
    /// to retry or replan.
    /// </returns>
    Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default);
}