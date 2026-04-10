using SwBridge.Connection;

namespace SwBridge.Tools.Macro;

/// <summary>
/// Lists all SWBasic macro files (.swb) available in the macros directory.
/// The LLM calls this to discover what macros already exist before deciding
/// whether to write a new one or run an existing one.
/// </summary>
/// <remarks>
/// Only .swb (SWBasic plain-text) macros are listed. Binary .swp macros
/// are not supported because the LLM cannot read or write their format.
/// </remarks>
public sealed class ListMacrosTool : ISwTool
{
    private readonly string _macrosDirectory;

    /// <summary>
    /// Initializes the tool with the path to the macros directory.
    /// </summary>
    /// <param name="macrosDirectory">
    /// Absolute path to the folder where .swb macro files are stored.
    /// Typically the /macros directory at the repository root.
    /// </param>
    public ListMacrosTool(string macrosDirectory)
    {
        _macrosDirectory = macrosDirectory;
    }

    /// <inheritdoc/>
    public string Name => "list_macros";

    /// <inheritdoc/>
    public string Description =>
        "Lists all available SWBasic (.swb) macro files in the macros directory. " +
        "Returns each macro's filename and last-modified timestamp. " +
        "No parameters required. " +
        "Use this before writing a new macro to check if a suitable one already exists.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_macrosDirectory))
            return Task.FromResult(
                "ERROR: Macros directory does not exist. " +
                $"Expected path: {_macrosDirectory}");

        var macros = Directory
            .GetFiles(_macrosDirectory, "*.swb", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.Name} (modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss})";
            })
            .ToList();

        if (macros.Count == 0)
            return Task.FromResult("No macros found in the macros directory.");

        return Task.FromResult(
            $"Found {macros.Count} macro(s):\n" + string.Join("\n", macros));
    }
}
