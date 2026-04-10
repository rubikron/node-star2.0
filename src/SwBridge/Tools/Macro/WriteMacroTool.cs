using System.Text;
using System.Text.RegularExpressions;

namespace SwBridge.Tools.Macro;

/// <summary>
/// Writes a text-based SOLIDWORKS macro to disk.
/// The macro must be a single-module <c>.swb</c> file with <c>Sub main()</c>.
/// </summary>
/// <remarks>
/// The tool generates the metadata header and overwrites existing files.
/// </remarks>
public sealed class WriteMacroTool : ISwTool
{
    /// <summary>
    /// Invalid filename characters for Windows macro files.
    /// </summary>
    private static readonly char[] InvalidFilenameChars =
        Path.GetInvalidFileNameChars();

    /// <summary>
    /// Matches a parameterless <c>Sub main()</c> declaration.
    /// </summary>
    private static readonly Regex SubMainPattern = new(
        @"^\s*Sub\s+main\s*\(\s*\)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches unsupported class or form module markers.
    /// </summary>
    private static readonly Regex InvalidModulePattern = new(
        @"^\s*(BEGIN\s+\{|VERSION\s+\d+|Attribute\s+VB_Name)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _macrosDirectory;

    static WriteMacroTool()
    {
        // .NET 5+ only includes UTF and ASCII encodings by default.
        // Windows-1252 (ANSI) is required for SOLIDWORKS .swb compatibility.
        // The VBA engine treats a UTF-8 BOM as literal characters, causing
        // a parse error before Sub main() is reached.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Initializes the tool with the macros directory path.
    /// </summary>
    /// <param name="macrosDirectory">Directory where <c>.swb</c> files are stored.</param>
    public WriteMacroTool(string macrosDirectory)
    {
        _macrosDirectory = macrosDirectory;
    }

    /// <inheritdoc/>
    public string Name => "write_macro";

    /// <inheritdoc/>
    public string Description =>
        "Writes a SWBasic VBA macro (.swb) to the macros directory. " +
        "The macro can then be executed with run_macro. " +
        "Parameters: " +
        "  'filename'    (required) - output filename, e.g. 'create_bracket.swb'. " +
        "  'macro_name'  (required) - human-readable name, e.g. 'Create Mounting Bracket'. " +
        "  'description' (required) - what the macro does and any preconditions. " +
        "  'code'        (required) - the full VBA macro body. Must contain Sub main() " +
        "                             as the entry point (run_macro always calls 'main'). " +
        "                             Must not contain class modules or user forms. " +
        "                             Should include On Error GoTo handling. " +
        "                             The standard header is generated automatically. " +
        "Returns 'SUCCESS: <filename>' on success or an 'ERROR:' message if validation fails.";

    /// <inheritdoc/>
    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        //  Extract and validate parameters

        if (!parameters.TryGetValue("filename", out var filename) ||
            string.IsNullOrWhiteSpace(filename))
            return "ERROR: Missing required parameter 'filename'.";

        if (!parameters.TryGetValue("macro_name", out var macroName) ||
            string.IsNullOrWhiteSpace(macroName))
            return "ERROR: Missing required parameter 'macro_name'.";

        if (!parameters.TryGetValue("description", out var description) ||
            string.IsNullOrWhiteSpace(description))
            return "ERROR: Missing required parameter 'description'.";

        if (!parameters.TryGetValue("code", out var code) ||
            string.IsNullOrWhiteSpace(code))
            return "ERROR: Missing required parameter 'code'.";

        //  Sanitize filename

        filename = filename.Trim();

        if (filename.IndexOfAny(InvalidFilenameChars) >= 0 || filename.Contains(".."))
            return "ERROR: Invalid filename. Use only alphanumeric characters, " +
                   "underscores, and hyphens, e.g. 'create_bracket.swb'.";

        if (!filename.EndsWith(".swb", StringComparison.OrdinalIgnoreCase))
            filename += ".swb";

        var fullPath = Path.GetFullPath(Path.Combine(_macrosDirectory, filename));

        if (!fullPath.StartsWith(
                Path.GetFullPath(_macrosDirectory),
                StringComparison.OrdinalIgnoreCase))
            return "ERROR: Resolved path is outside the macros directory.";

        //  Validate VBA code structure

        if (!SubMainPattern.IsMatch(code))
            return "ERROR: The code must contain 'Sub main()' as the entry point. " +
                   "run_macro always invokes the procedure named 'main'.";

        if (InvalidModulePattern.IsMatch(code))
            return "ERROR: The code contains class module or UserForm markers. " +
                   ".swb macros support only a single plain code module. " +
                   "Remove any class or form definitions.";

        //  Warn if no error handler is present (non-blocking)

        var errorHandlerWarning = string.Empty;
        if (!code.Contains("On Error GoTo", StringComparison.OrdinalIgnoreCase))
            errorHandlerWarning =
                "\nWARNING: No 'On Error GoTo' handler found. Runtime errors will " +
                "surface as SOLIDWORKS dialogs rather than text returned to the LLM. " +
                "Consider rewriting with error handling.";

        //  Build the complete .swb file content

        var content = BuildMacroContent(macroName, description, code);

        //  Write to disk

        try
        {
            Directory.CreateDirectory(_macrosDirectory);

            await File.WriteAllTextAsync(
                fullPath,
                content,
                Encoding.GetEncoding(1252),
                cancellationToken);

            return $"SUCCESS: Macro written to '{filename}'.{errorHandlerWarning}";
        }
        catch (IOException ex)
        {
            return $"ERROR: Could not write '{filename}' - {ex.Message}.";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"ERROR: Access denied writing '{filename}' - {ex.Message}.";
        }
    }

    /// <summary>
    /// Builds the final <c>.swb</c> content, including the metadata header.
    /// </summary>
    /// <param name="macroName">Value for the <c>@name</c> tag.</param>
    /// <param name="description">Value for the <c>@description</c> tag.</param>
    /// <param name="code">Macro body written after the header.</param>
    /// <returns>The full file content.</returns>
    private static string BuildMacroContent(
        string macroName,
        string description,
        string code)
    {
        var sb = new StringBuilder();

        // ---- Metadata header ------------------------------------------- //
        sb.AppendLine($"' @name        {macroName}");

        // Word-wrap long descriptions at 72 characters so the file stays readable.
        var descriptionLines = WordWrap(description, maxWidth: 72);
        for (var i = 0; i < descriptionLines.Count; i++)
        {
            sb.AppendLine(i == 0
                ? $"' @description {descriptionLines[i]}"
                : $"'              {descriptionLines[i]}");
        }

        sb.AppendLine($"' @author      nodestar");
        sb.AppendLine($"' @created     {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine("'");
        sb.AppendLine(code.TrimEnd());

        return sb.ToString();
    }

    /// <summary>
    /// Wraps text to a maximum line width.
    /// </summary>
    /// <param name="text">The text to wrap.</param>
    /// <param name="maxWidth">Maximum characters per line.</param>
    /// <returns>List of wrapped lines, never empty.</returns>
    private static List<string> WordWrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length > 0 &&
                current.Length + 1 + word.Length > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');

            current.Append(word);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        if (lines.Count == 0)
            lines.Add(string.Empty);

        return lines;
    }
}
