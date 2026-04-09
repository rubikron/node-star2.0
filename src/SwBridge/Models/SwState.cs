namespace SwBridge.Models;

/// <summary>
/// Represents a snapshot of the current SOLIDWORKS session state,
/// serialized and passed to the LLM so it understands what is currently
/// open and active in the application.
/// </summary>
public class SwState
{
    // TODO: populate as tool layer is built out.
    // Expected fields: active document path, document type (Part/Assembly/Drawing),
    // open document list, current selection, active configuration name, etc.
}