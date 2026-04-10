using SwBridge.Connection;

namespace SwBridge.Tools.Macro;

/// <summary>
/// Lists all SWBasic macro files (.swb) in the macros directory, along
/// with their embedded metadata headers so the LLM can understand what
/// each macro does before deciding to run or replace it.
///
/// Metadata is read from structured comment lines at the top of each
/// .swb file using the convention:
/// <code>
/// ' @name        Human-readable macro name
/// ' @description What this macro does and any preconditions.
///                Continuation lines are indented and have no tag.
/// ' @author      Who or what wrote it
/// ' @created     ISO date (yyyy-MM-dd)
/// </code>
/// Lines not beginning with a recognised tag are ignored.
/// Only .swb plain-text macros are listed — binary .swp files are not
/// supported because the LLM cannot read or write their format.
/// </summary>
public sealed class ListMacrosTool : ISwTool
{
    /// <summary>Maximum lines to scan per file when extracting metadata.</summary>
    /// <remarks>
    /// Header comments always appear before Sub main(). Capping the scan
    /// avoids reading large macro bodies for files that omit a header.
    /// </remarks>
    private const int MaxHeaderLines = 30;

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
        "Returns each macro's filename, embedded name, description, author, and " +
        "creation date extracted from its header comments. " +
        "No parameters required. " +
        "Call this before writing a new macro to check whether a suitable one already exists.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_macrosDirectory))
            return Task.FromResult(
                "ERROR: Macros directory does not exist. " +
                $"Expected path: {_macrosDirectory}");

        var files = Directory
            .GetFiles(_macrosDirectory, "*.swb", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            return Task.FromResult(
                "No macros found in the macros directory.");

        var entries = files.Select(path => FormatMacroEntry(path)).ToList();

        return Task.FromResult(
            $"Found {files.Count} macro(s):\n\n" +
            string.Join("\n\n", entries));
    }

    /// <summary>
    /// Reads the header metadata from a .swb file and formats it as a
    /// readable entry for the LLM, falling back gracefully when a tag
    /// is missing or the file cannot be read.
    /// </summary>
    /// <param name="filePath">Absolute path to the .swb file.</param>
    /// <returns>A formatted multi-line string describing the macro.</returns>
    private static string FormatMacroEntry(string filePath)
    {
        var info = new FileInfo(filePath);
        var meta = ExtractMetadata(filePath);

        var name        = meta.GetValueOrDefault("name",        "(no name)");
        var description = meta.GetValueOrDefault("description", "(no description)");
        var author      = meta.GetValueOrDefault("author",      "(unknown)");
        var created     = meta.GetValueOrDefault("created",     "(unknown)");

        return
            $"File:        {info.Name}\n" +
            $"Name:        {name}\n" +
            $"Description: {description}\n" +
            $"Author:      {author}\n" +
            $"Created:     {created}\n" +
            $"Modified:    {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    }

    /// <summary>
    /// Parses the structured comment header of a .swb file and returns
    /// a dictionary of tag → value pairs.
    ///
    /// Supports multi-line descriptions: if a comment line after a tag
    /// line has no tag of its own, its content is appended to the most
    /// recently seen tag's value.
    /// </summary>
    /// <param name="filePath">Absolute path to the .swb file to parse.</param>
    /// <returns>
    /// Dictionary of lowercase tag names to their extracted values.
    /// Empty if the file cannot be read or contains no recognised tags.
    /// </returns>
    private static Dictionary<string, string> ExtractMetadata(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastTag = null;

        try
        {
            var lines = File.ReadLines(filePath).Take(MaxHeaderLines);

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                // Stop scanning once we hit actual code.
                if (line.StartsWith("Sub ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Function ", StringComparison.OrdinalIgnoreCase))
                    break;

                // Only process VBA comment lines.
                if (!line.StartsWith("'"))
                    continue;

                // Strip the leading apostrophe and any surrounding whitespace.
                var content = line[1..].Trim();

                if (content.StartsWith('@'))
                {
                    // e.g. "@description Creates a bracket..."
                    var spaceIdx = content.IndexOf(' ');
                    if (spaceIdx < 0)
                        continue;

                    lastTag = content[1..spaceIdx].ToLowerInvariant();
                    var value = content[(spaceIdx + 1)..].Trim();
                    result[lastTag] = value;
                }
                else if (lastTag is not null && content.Length > 0)
                {
                    // Continuation line — append to the previous tag's value.
                    result[lastTag] = result[lastTag] + " " + content;
                }
            }
        }
        catch (IOException)
        {
            // File locked or unreadable — return whatever we have so far.
        }

        return result;
    }
}
