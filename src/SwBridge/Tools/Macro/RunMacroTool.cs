using SolidWorks.Interop.swconst;
using SwBridge.Connection;

namespace SwBridge.Tools.Macro;

/// <summary>
/// Executes a SWBasic (.swb) macro file using the SOLIDWORKS
/// <c>ISldWorks::RunMacro2</c> API. The macro must follow the
/// standard convention enforced by WriteMacroTool: a single module
/// named <c>MainModule</c> with a parameterless <c>main</c> sub as
/// the entry point.
/// </summary>
/// <remarks>
/// RunMacro2 requires both a module name and a procedure name.
/// For LLM-generated .swb macros the module is always "MainModule"
/// and the entry sub is always "main", so neither needs to be
/// specified by the LLM — this tool hard-codes both conventions.
///
/// Macro execution is synchronous from the SW API perspective.
/// RunMacro2 blocks until the macro finishes or errors.
/// </remarks>
public sealed class RunMacroTool : ISwTool
{
    // SW API convention for LLM-generated .swb macros.
    private const string ModuleName    = "MainModule";
    private const string ProcedureName = "main";

    private readonly ISwConnector _connector;
    private readonly string _macrosDirectory;

    /// <summary>
    /// Initializes the tool with a live connector and the macros directory path.
    /// </summary>
    /// <param name="connector">
    /// The active SOLIDWORKS connector. Must be in
    /// <see cref="SwConnectionState.Ready"/> state before calling
    /// <see cref="ExecuteAsync"/>.
    /// </param>
    /// <param name="macrosDirectory">
    /// Absolute path to the folder containing .swb macro files.
    /// </param>
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
        "  'filename' (required) — the .swb filename, e.g. 'create_bracket.swb'. " +
        "Returns 'SUCCESS' if the macro ran without errors, or an 'ERROR:' message " +
        "with the SOLIDWORKS error code if execution failed. " +
        "The macro must have been written using write_macro first.";

    /// <inheritdoc/>
    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        // ---------------------------------------------------------------- //
        //  Validate connection state
        // ---------------------------------------------------------------- //
        if (_connector.State != SwConnectionState.Ready || _connector.Application is null)
            return Task.FromResult(
                "ERROR: SOLIDWORKS is not connected. " +
                $"Current state: {_connector.State}.");

        // ---------------------------------------------------------------- //
        //  Validate parameters
        // ---------------------------------------------------------------- //
        if (!parameters.TryGetValue("filename", out var filename) ||
            string.IsNullOrWhiteSpace(filename))
            return Task.FromResult(
                "ERROR: Missing required parameter 'filename'. " +
                "Provide the .swb filename, e.g. 'create_bracket.swb'.");

        // Prevent path traversal — only allow simple filenames,
        // no directory separators or relative path components.
        if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            filename.Contains(".."))
            return Task.FromResult(
                "ERROR: Invalid filename. " +
                "Provide just the filename with no path components, e.g. 'my_macro.swb'.");

        // Enforce .swb extension — binary .swp macros are not supported.
        if (!filename.EndsWith(".swb", StringComparison.OrdinalIgnoreCase))
            filename += ".swb";

        var fullPath = Path.Combine(_macrosDirectory, filename);

        if (!File.Exists(fullPath))
            return Task.FromResult(
                $"ERROR: Macro file '{filename}' not found in the macros directory. " +
                "Use list_macros to see available macros, or write_macro to create one.");

        // ---------------------------------------------------------------- //
        //  Execute via RunMacro2
        // ---------------------------------------------------------------- //
        // RunMacro2 is synchronous — it blocks until the macro finishes.
        // We wrap it in Task.Run so it doesn't block the UI thread while
        // a long-running macro (e.g. building a complex assembly) executes.
        return Task.Run(() => RunMacro(fullPath), cancellationToken);
    }

    /// <summary>
    /// Calls <c>ISldWorks::RunMacro2</c> on the active SOLIDWORKS instance
    /// and maps the error code to a human-readable result string.
    /// </summary>
    /// <param name="fullPath">Absolute path to the .swb file to run.</param>
    /// <returns>
    /// "SUCCESS" on clean execution, or an "ERROR:" string describing
    /// the SOLIDWORKS error code so the LLM can diagnose and fix the macro.
    /// </returns>
    private string RunMacro(string fullPath)
    {
        var app = _connector.Application!;
        int errorCode;

        // swRunMacroUnloadAfterRun ensures the macro is fully unloaded from
        // memory after execution, preventing stale state across repeated runs.
        bool success = app.RunMacro2(
            fullPath,
            ModuleName,
            ProcedureName,
            (int)swRunMacroOption_e.swRunMacroUnloadAfterRun,
            out errorCode);

        if (success)
            return "SUCCESS";

        // Map SW error codes to readable messages so the LLM can self-correct.
        var errorDescription = (swRunMacroError_e)errorCode switch
        {
            // The macro file itself couldn't be opened
            swRunMacroError_e.swRunMacroError_OpenFileFailed
                => "macro file could not be opened — it may be corrupted or locked by another process",
            swRunMacroError_e.swRunMacroError_DiskError
                => "disk error reading the macro file",
            swRunMacroError_e.swRunMacroError_TooManyOpenFiles
                => "too many files open — SOLIDWORKS could not load the macro",

            // The macro code itself has problems the LLM can fix
            swRunMacroError_e.swRunMacroError_InvalidProcname
                => $"procedure '{ProcedureName}' not found in module '{ModuleName}' — " +
                "ensure the macro has a Sub named 'main' inside a module named 'MainModule'",
            swRunMacroError_e.swRunMacroError_OnlyCodeModules
                => "macro contains non-code modules (forms or class modules) — " +
                ".swb macros support only a single code module",
            swRunMacroError_e.swRunMacroError_SuborfuncExpected
                => "entry point is not a Sub or Function — 'main' must be declared as Sub main()",
            swRunMacroError_e.swRunMacroError_BadParmCount
                => "entry Sub 'main' must take no parameters",
            swRunMacroError_e.swRunMacroError_BadVarType
                => "a variable type in the macro is invalid for the current SOLIDWORKS version",
            swRunMacroError_e.swRunMacroError_InvalidPropertyType
                => "a property type used in the macro is invalid",
            swRunMacroError_e.swRunMacroError_ParmNotOptional
                => "a required parameter was not supplied in a macro call",
            swRunMacroError_e.swRunMacroError_TypeMismatch
                => "type mismatch — a variable was assigned a value of the wrong type",
            swRunMacroError_e.swRunMacroError_Overflow
                => "arithmetic overflow in the macro — check numeric variable bounds",
            swRunMacroError_e.swRunMacroError_OutOfMemory
                => "SOLIDWORKS ran out of memory executing the macro",

            // Runtime failures
            swRunMacroError_e.swRunMacroError_Exception
                => "unhandled exception thrown during macro execution — " +
                "add error handling to the macro (On Error GoTo handler)",
            swRunMacroError_e.swRunMacroError_UserInterrupt
                => "macro was interrupted by the user",
            swRunMacroError_e.swRunMacroError_CallFailed
                => "a SOLIDWORKS API call inside the macro returned a failure",
            swRunMacroError_e.swRunMacroError_CallRejected
                => "a SOLIDWORKS API call was rejected — " +
                "SW may be busy or a dialog may be blocking execution",
            swRunMacroError_e.swRunMacroError_Busy
                => "SOLIDWORKS is busy and cannot run a macro right now — retry after the current operation completes",

            // Permission and environment problems
            swRunMacroError_e.swRunMacroError_MacrosAreDisabled
                => "macros are disabled in SOLIDWORKS — enable them under Tools > Options > System Options > General",
            swRunMacroError_e.swRunMacroError_NoPermission
                => "insufficient permissions to run this macro",
            swRunMacroError_e.swRunMacroError_NotInDesignMode
                => "macro cannot run because SOLIDWORKS is not in design mode",
            swRunMacroError_e.swRunMacroError_InvalidArg
                => "an invalid argument was passed to RunMacro2 — this is a SwBridge bug, not a macro bug",

            // COM/connection failures
            swRunMacroError_e.swRunMacroError_ConnectionTerminated
                => "COM connection to SOLIDWORKS was terminated during macro execution",
            swRunMacroError_e.swRunMacroError_Zombied
                => "the SOLIDWORKS COM object is in a zombie state — the session may need to be restarted",
            swRunMacroError_e.swRunMacroError_Reverted
                => "macro execution was reverted",

            // Misc
            swRunMacroError_e.swRunMacroError_CantSave
                => "SOLIDWORKS could not save state after macro execution",
            swRunMacroError_e.swRunMacroError_Invalidindex
                => "an invalid index was used in the macro",
            swRunMacroError_e.swRunMacroError_UnknownLcid
                => "unknown locale identifier — locale mismatch between the macro and SOLIDWORKS",

            _ => $"undocumented error code {errorCode}"
        };

        return $"ERROR: Macro execution failed — {errorDescription}. " +
               "Fix the macro using write_macro and try again.";
    }
}
