using SwBridge.Connection;

// -----------------------------------------------------------------------
// Manual integration test: launches SOLIDWORKS, waits 60 seconds, exits.
// Run from repo root with:
//   dotnet run --project tests/SwBridge.ManualTest
// -----------------------------------------------------------------------

const string SwPath = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
const int HoldSeconds = 60;

using var connector = new SwConnector();

// Subscribe to state changes so we can see transitions in the console.
connector.StateChanged += (_, state) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] State changed → {state}");

using var cts = new CancellationTokenSource();

// Allow Ctrl+C to cancel the launch if SW hangs on startup.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Cancellation requested...");
    cts.Cancel();
};

try
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Launching SOLIDWORKS...");
    await connector.ConnectAsync(SwPath, cts.Token);

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connected. " +
        $"Revision: {connector.RevisionNumber}");
    Console.WriteLine($"Holding for {HoldSeconds}s — press Ctrl+C to cancel early.");

    await Task.Delay(TimeSpan.FromSeconds(HoldSeconds), cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Launch cancelled by user.");
}
catch (TimeoutException ex)
{
    Console.WriteLine($"TIMEOUT: {ex.Message}");
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"BAD PATH: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"UNEXPECTED ERROR: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Disconnecting...");
    await connector.DisconnectAsync();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Done.");
}