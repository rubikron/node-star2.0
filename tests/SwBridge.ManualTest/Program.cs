using SwBridge.Connection;
using SwBridge.Tools.Macro;
using SwBridge.Tools.Session;

// -----------------------------------------------------------------------
// Manual integration test: launches SOLIDWORKS, writes three simple macros,
// runs each one, lists them, then deletes them.
//
// Run from repo root with:
//   dotnet run --project tests/SwBridge.ManualTest
// -----------------------------------------------------------------------

const string SwPath       = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
const string MacrosDir    = @".\macros_integration_test";
const int    HoldSeconds  = 10;

Directory.CreateDirectory(MacrosDir);

var writeTool  = new WriteMacroTool(MacrosDir);
var listTool   = new ListMacrosTool(MacrosDir);
var deleteTool = new DeleteMacroTool(MacrosDir);

using var connector = new SwConnector();
using var cts       = new CancellationTokenSource();

connector.StateChanged += (_, state) =>
    Log($"SW state → {state}");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log("Cancellation requested...");
    cts.Cancel();
};

try
{
    // ---------------------------------------------------------------- //
    //  Step 1: Write three test macros
    // ---------------------------------------------------------------- //

    Log("Writing test macros...");

    // Macro 1: shows a message box — works whether a document is open or not.
    await Write("hello_world.swb", "Hello World",
        "Displays a simple message box to confirm macro execution works.",
        """
        Sub main()
            On Error GoTo ErrorHandler
            Dim swApp As Object
            Set swApp = Application.SldWorks
            swApp.SendMsgToUser "Hello from Nodestar macro runner!"
            Exit Sub
        ErrorHandler:
            MsgBox "Error: " & Err.Description
        End Sub
        """);

    // Macro 2: creates a new part document and a 10mm cube.
    await Write("create_cube.swb", "Create 10mm Cube",
        "Opens a new part document and creates a 10mm cube via a sketch extrude.",
        """
        Sub main()
            On Error GoTo ErrorHandler

            Dim swApp  As Object
            Dim swDoc  As Object
            Dim swSketchMgr As Object
            Dim swFeatMgr   As Object
            Dim swSketch    As Object

            Set swApp = Application.SldWorks

            ' Open a new part document.
            swApp.NewPart
            Set swDoc = swApp.ActiveDoc

            If swDoc Is Nothing Then
                MsgBox "Failed to create new part."
                Exit Sub
            End If

            Set swSketchMgr = swDoc.SketchManager
            Set swFeatMgr   = swDoc.FeatureManager

            ' Insert sketch on the Front plane (index 0).
            swDoc.Extension.SelectByID2 "Front Plane", "PLANE", 0, 0, 0, False, 0, Nothing, 0
            swSketchMgr.InsertSketch True

            ' Draw a 10mm x 10mm rectangle centred at origin.
            swSketchMgr.CreateCenterRectangle 0, 0, 0, 0.005, 0.005, 0

            ' Exit sketch and extrude 10mm.
            swSketchMgr.InsertSketch True
            swFeatMgr.FeatureExtrusion2 True, False, False, 0, 0, 0.01, 0.01, _
                False, False, False, False, 0, 0, False, False, False, False, True, True, True, 0, 0, False

            ' swDoc.Save3 1, 0, 0
            swApp.SendMsgToUser "10mm cube created successfully."
            Exit Sub
        ErrorHandler:
            MsgBox "Cube macro error: " & Err.Description
        End Sub
        """);

    // Macro 3: reports the active document name — safe, read-only.
    await Write("report_active_doc.swb", "Report Active Document",
        "Reads and reports the name of the currently active SOLIDWORKS document.",
        """
        Sub main()
            On Error GoTo ErrorHandler

            Dim swApp As Object
            Dim swDoc As Object

            Set swApp = Application.SldWorks
            Set swDoc = swApp.ActiveDoc

            If swDoc Is Nothing Then
                swApp.SendMsgToUser "No document is currently active."
            Else
                swApp.SendMsgToUser "Active document: " & swDoc.GetTitle()
            End If

            Exit Sub
        ErrorHandler:
            MsgBox "Error: " & Err.Description
        End Sub
        """);

    Log("All macros written.");

    // ---------------------------------------------------------------- //
    //  Step 2: List macros before connecting to SW
    // ---------------------------------------------------------------- //

    Log("Listing macros (pre-launch):");
    var listing = await listTool.ExecuteAsync(new Dictionary<string, string>());
    Console.WriteLine(listing);

    // ---------------------------------------------------------------- //
    //  Step 3: Launch SOLIDWORKS
    // ---------------------------------------------------------------- //

    Log("Launching SOLIDWORKS...");
    await connector.ConnectAsync(SwPath, cts.Token);
    Log($"Connected. Revision: {connector.RevisionNumber}");

    var runTool = new RunMacroTool(connector, MacrosDir);
    var stateTool = new GetSwStateTool(connector);

    Log("Initial full state snapshot:");
    Console.WriteLine(
        await stateTool.ExecuteAsync(
            new Dictionary<string, string> { ["mode"] = "full" },
            cts.Token));

    // ---------------------------------------------------------------- //
    //  Step 4: Run each macro
    // ---------------------------------------------------------------- //

    await Task.Delay(TimeSpan.FromSeconds(HoldSeconds), cts.Token);
    await Run(runTool, "hello_world.swb");
    await DumpPatch(stateTool);

    // Brief pause between macros — SW needs a moment to settle.
    await Task.Delay(TimeSpan.FromSeconds(HoldSeconds), cts.Token);

    await Run(runTool, "create_cube.swb");
    await DumpPatch(stateTool);
    await Task.Delay(TimeSpan.FromSeconds(HoldSeconds), cts.Token);

    await Run(runTool, "report_active_doc.swb");
    await DumpPatch(stateTool);
    await Task.Delay(TimeSpan.FromSeconds(HoldSeconds), cts.Token);

    // ---------------------------------------------------------------- //
    //  Step 5: Delete all test macros
    // ---------------------------------------------------------------- //

    Log("Deleting test macros...");
    await Delete("hello_world.swb");
    await Delete("create_cube.swb");
    await Delete("report_active_doc.swb");

    // Confirm directory is empty.
    Log("Listing macros (post-delete):");
    var postDelete = await listTool.ExecuteAsync(new Dictionary<string, string>());
    Console.WriteLine(postDelete);
}
catch (OperationCanceledException)
{
    Log("Cancelled by user.");
}
catch (Exception ex)
{
    Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    Log("Disconnecting from SOLIDWORKS...");
    await connector.DisconnectAsync();
    Log("Done.");

    // Clean up the temp macros directory.
    if (Directory.Exists(MacrosDir))
        Directory.Delete(MacrosDir, recursive: true);
}

// ---------------------------------------------------------------- //
//  Helpers
// ---------------------------------------------------------------- //

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

async Task Write(string filename, string name, string description, string code)
{
    var result = await writeTool.ExecuteAsync(new Dictionary<string, string>
    {
        ["filename"]    = filename,
        ["macro_name"]  = name,
        ["description"] = description,
        ["code"]        = code
    });
    Log($"write_macro({filename}) → {result}");
}

async Task Run(RunMacroTool tool, string filename)
{
    Log($"run_macro({filename})...");
    var result = await tool.ExecuteAsync(new Dictionary<string, string>
    {
        ["filename"] = filename
    });
    Log($"run_macro({filename}) → {result}");
}

async Task Delete(string filename)
{
    var result = await deleteTool.ExecuteAsync(new Dictionary<string, string>
    {
        ["filename"] = filename
    });
    Log($"delete_macro({filename}) → {result}");
}

async Task DumpPatch(GetSwStateTool tool)
{
    Log("get_sw_state(mode=patch)...");
    var result = await tool.ExecuteAsync(
        new Dictionary<string, string> { ["mode"] = "patch" },
        cts.Token);
    Console.WriteLine(result);
}
