namespace SwBridge.Tools.Macro;

/// <summary>
/// Permanently deletes a SWBasic (.swb) macro file from the macros
/// directory. The LLM calls this to clean up obsolete or broken macros
/// before replacing them with an updated version via write_macro.
/// </summary>
/// <remarks>
/// Deletion is permanent — there is no recycle bin step. The LLM should
/// call list_macros first to confirm the filename before deleting.
/// Only files inside the configured macros directory can be deleted;
/// path traversal attempts are rejected before any filesystem access.
/// </remarks>
public sealed class DeleteMacroTool : ISwTool
{
    private readonly string _macrosDirectory;

    /// <summary>
    /// Initializes the tool with the path to the macros directory.
    /// </summary>
    /// <param name="macrosDirectory">
    /// Absolute path to the folder where .swb macro files are stored.
    /// </param>
    public DeleteMacroTool(string macrosDirectory)
    {
        _macrosDirectory = macrosDirectory;
    }

    /// <inheritdoc/>
    public string Name => "delete_macro";

    /// <inheritdoc/>
    public string Description =>
        "Permanently deletes a SWBasic (.swb) macro file from the macros directory. " +
        "Parameters: " +
        "  'filename' (required) — the .swb filename to delete, e.g. 'old_bracket.swb'. " +
        "Returns 'SUCCESS' if deleted, or an 'ERROR:' message if the file was not found " +
        "or could not be deleted. " +
        "Use list_macros first to confirm the filename. " +
        "Prefer calling this before write_macro when replacing a broken macro.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!parameters.TryGetValue("filename", out var filename) ||
            string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(
                "ERROR: Missing required parameter 'filename'.");

        // Prevent path traversal — only simple filenames are accepted.
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            filename.Contains(".."))
            return Task.FromResult(
                "ERROR: Invalid filename. Provide just the filename with no path " +
                "components, e.g. 'my_macro.swb'.");

        if (!filename.EndsWith(".swb", StringComparison.OrdinalIgnoreCase))
            filename += ".swb";

        var fullPath = Path.Combine(_macrosDirectory, filename);

        // Verify the resolved path is still inside the macros directory
        // even after Path.Combine normalisation.
        if (!fullPath.StartsWith(
                Path.GetFullPath(_macrosDirectory),
                StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(
                "ERROR: Resolved path is outside the macros directory.");

        if (!File.Exists(fullPath))
            return Task.FromResult(
                $"ERROR: Macro '{filename}' not found. " +
                "Use list_macros to see available macros.");

        try
        {
            File.Delete(fullPath);
            return Task.FromResult("SUCCESS");
        }
        catch (IOException ex)
        {
            return Task.FromResult(
                $"ERROR: Could not delete '{filename}' — {ex.Message}. " +
                "The file may be locked by SOLIDWORKS if it was recently run.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(
                $"ERROR: Access denied deleting '{filename}' — {ex.Message}.");
        }
    }
}
