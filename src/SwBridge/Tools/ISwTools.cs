namespace SwBridge.Tools;

/// <summary>
/// Defines the contract for a single SOLIDWORKS operation exposed to the
/// LLM as a callable tool. Each implementation wraps one discrete API
/// action (e.g. open document, create extrude, save file).
/// </summary>
public interface ISwTool
{
    // TODO: define once LlmOrchestrator tool schema is agreed upon with team.
    // Expected members: Name, Description, Execute(Dictionary<string, object>).
}