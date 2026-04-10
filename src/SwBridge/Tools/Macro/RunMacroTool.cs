using SolidWorks.Interop.swconst;
using SwBridge.Connection;

namespace SwBridge.Tools.Macro;

/// <summary>
/// Runs a text-based SOLIDWORKS macro through <c>ISldWorks.RunMacro2</c>.
/// The entry procedure is always <c>main</c>.
/// </summary>
/// <remarks>
/// For <c>.swb</c> files, SOLIDWORKS derives the module name from the filename.
/// </remarks>
public sealed class RunMacroTool : ISwTool
{
    // The entry-point procedure name for all LLM-generated macros.
    // The module name is not hardcoded - for .swb text macros SOLIDWORKS
    // derives it from the filename without extension (for example "hello_world"),
    // so it is computed per call in RunMacro().
    private const string ProcedureName = "main";

    private readonly ISwConnector _connector;
    private readonly string _macrosDirectory;

    /// <summary>
    /// Initializes the tool with a connector and macros directory.
    /// </summary>
    /// <param name="connector">Active SOLIDWORKS connector.</param>
    /// <param name="macrosDirectory">Directory containing <c>.swb</c> files.</param>
    public RunMacroTool(ISwConnector connector, string macrosDirectory)
    {
        _connector = connector;
        _macrosDirectory = macrosDirectory;
    }

    /// <inheritdoc/>
    public string Name => "run_macro";

    /// <inheritdoc/>
    public string Description =>
        "Runs a SWBasic (.swb) macro file that exists in the macros directory. " +
        "Parameters: " +
        "  'filename' (required) - the .swb filename, e.g. 'create_bracket.swb'. " +
        "Returns 'SUCCESS' if the macro ran without errors, or an 'ERROR:' message " +
        "with the SOLIDWORKS error code if execution failed. " +
        "The macro must have been written using write_macro first. " +
        "The module name is derived from the filename automatically.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        //  Validate connection state
        if (_connector.State != SwConnectionState.Ready || _connector.Application is null)
            return Task.FromResult(
                "ERROR: SOLIDWORKS is not connected. " +
                $"Current state: {_connector.State}.");

        //  Validate parameters
        if (!parameters.TryGetValue("filename", out var filename) ||
            string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(
                "ERROR: Missing required parameter 'filename'. " +
                "Provide the .swb filename, e.g. 'create_bracket.swb'.");

        // Prevent path traversal - only allow simple filenames.
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            filename.Contains(".."))
            return Task.FromResult(
                "ERROR: Invalid filename. " +
                "Provide just the filename with no path components, e.g. 'my_macro.swb'.");

        // Enforce .swb extension - binary .swp macros are not supported.
        if (!filename.EndsWith(".swb", StringComparison.OrdinalIgnoreCase))
            filename += ".swb";

        var fullPath = Path.GetFullPath(Path.Combine(_macrosDirectory, filename));

        if (!File.Exists(fullPath))
            return Task.FromResult(
                $"ERROR: Macro file '{filename}' not found in the macros directory. " +
                "Use list_macros to see available macros, or write_macro to create one.");

        //  Execute via RunMacro2
        // RunMacro2 is synchronous. Wrap it so it does not block the UI thread.
        return Task.Run(() => RunMacro(fullPath), cancellationToken);
    }

    /// <summary>
    /// Calls <c>RunMacro2</c> and maps the error code to a readable result.
    /// </summary>
    /// <param name="fullPath">Absolute path to the <c>.swb</c> file to run.</param>
    /// <returns><c>SUCCESS</c> or an <c>ERROR:</c> message.</returns>
    private string RunMacro(string fullPath)
    {
        var app = _connector.Application!;
        int errorCode;

        // For .swb text macros, SOLIDWORKS uses the filename as the module name.
        var moduleName = Path.GetFileNameWithoutExtension(fullPath);

        // Unload after each run to avoid stale macro state.
        bool success = app.RunMacro2(
            fullPath,
            moduleName,
            ProcedureName,
            (int)swRunMacroOption_e.swRunMacroUnloadAfterRun,
            out errorCode);

        if (success)
            return "SUCCESS";

        var errorDescription = (swRunMacroError_e)errorCode switch
        {
            swRunMacroError_e.swRunMacroError_OpenFileFailed
                => "macro file could not be opened - it may be corrupted or locked by another process",
            swRunMacroError_e.swRunMacroError_DiskError
                => "disk error reading the macro file",
            swRunMacroError_e.swRunMacroError_TooManyOpenFiles
                => "too many files open - SOLIDWORKS could not load the macro",

            swRunMacroError_e.swRunMacroError_InvalidProcname
                => $"procedure '{ProcedureName}' not found in module '{moduleName}' - ensure the macro has a Sub named 'main'",
            swRunMacroError_e.swRunMacroError_OnlyCodeModules
                => "macro contains non-code modules (forms or class modules) - .swb macros support only a single code module",
            swRunMacroError_e.swRunMacroError_SuborfuncExpected
                => "entry point is not a Sub or Function - 'main' must be declared as Sub main()",
            swRunMacroError_e.swRunMacroError_BadParmCount
                => "entry Sub 'main' must take no parameters",
            swRunMacroError_e.swRunMacroError_BadVarType
                => "a variable type in the macro is invalid for the current SOLIDWORKS version",
            swRunMacroError_e.swRunMacroError_InvalidPropertyType
                => "a property type used in the macro is invalid",
            swRunMacroError_e.swRunMacroError_ParmNotOptional
                => "a required parameter was not supplied in a macro call",
            swRunMacroError_e.swRunMacroError_TypeMismatch
                => "type mismatch - a variable was assigned a value of the wrong type",
            swRunMacroError_e.swRunMacroError_Overflow
                => "arithmetic overflow in the macro - check numeric variable bounds",
            swRunMacroError_e.swRunMacroError_OutOfMemory
                => "SOLIDWORKS ran out of memory executing the macro",

            swRunMacroError_e.swRunMacroError_Exception
                => "unhandled exception thrown during macro execution - add error handling to the macro (On Error GoTo handler)",
            swRunMacroError_e.swRunMacroError_UserInterrupt
                => "macro was interrupted by the user",
            swRunMacroError_e.swRunMacroError_CallFailed
                => "a SOLIDWORKS API call inside the macro returned a failure",
            swRunMacroError_e.swRunMacroError_CallRejected
                => "a SOLIDWORKS API call was rejected - SOLIDWORKS may be busy or a dialog may be blocking execution",
            swRunMacroError_e.swRunMacroError_Busy
                => "SOLIDWORKS is busy and cannot run a macro right now - retry after the current operation completes",

            swRunMacroError_e.swRunMacroError_MacrosAreDisabled
                => "macros are disabled in SOLIDWORKS - enable them under Tools > Options > System Options > General",
            swRunMacroError_e.swRunMacroError_NoPermission
                => "insufficient permissions to run this macro",
            swRunMacroError_e.swRunMacroError_NotInDesignMode
                => "macro cannot run because SOLIDWORKS is not in design mode",
            swRunMacroError_e.swRunMacroError_InvalidArg
                => "an invalid argument was passed to RunMacro2 - this is a SwBridge bug, not a macro bug",

            swRunMacroError_e.swRunMacroError_ConnectionTerminated
                => "COM connection to SOLIDWORKS was terminated during macro execution",
            swRunMacroError_e.swRunMacroError_Zombied
                => "the SOLIDWORKS COM object is in a zombie state - the session may need to be restarted",
            swRunMacroError_e.swRunMacroError_Reverted
                => "macro execution was reverted",

            swRunMacroError_e.swRunMacroError_CantSave
                => "SOLIDWORKS could not save state after macro execution",
            swRunMacroError_e.swRunMacroError_Invalidindex
                => "an invalid index was used in the macro",
            swRunMacroError_e.swRunMacroError_UnknownLcid
                => "unknown locale identifier - locale mismatch between the macro and SOLIDWORKS",

            _ => $"undocumented error code {errorCode}"
        };

        return $"ERROR: Macro execution failed - {errorDescription}. " +
               "Fix the macro using write_macro and try again.";
    }
}
