using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidWorks.Interop.sldworks;
using SwBridge.Connection;

namespace SwBridge.Connection;

/// <summary>
/// Manages the lifecycle of a SOLIDWORKS application instance spawned
/// by this application. Uses the Windows Running Object Table (ROT) to
/// obtain a typed COM reference to the process after it has fully loaded,
/// which avoids the limitations of <c>Activator.CreateInstance</c> —
/// namely the <c>/embed</c> flag and unpredictable instance reuse.
/// </summary>
/// <remarks>
/// Both this application and the SOLIDWORKS process must run under the
/// same Windows user account. Mismatched elevation levels (e.g. one
/// running as Administrator) will cause ROT lookup to silently fail.
/// </remarks>
public sealed class SwConnector : ISwConnector, IDisposable
{
    // ------------------------------------------------------------------ //
    //  Windows native interop — required to query the Running Object Table
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Creates a COM binding context, which is required as an argument
    /// to <see cref="IRunningObjectTable"/> queries.
    /// </summary>
    /// <param name="reserved">Must be zero.</param>
    /// <param name="ppbc">Receives the new binding context.</param>
    /// <returns>HRESULT indicating success or failure.</returns>
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    // ------------------------------------------------------------------ //
    //  ROT polling configuration
    // ------------------------------------------------------------------ //

    /// <summary>
    /// How long to wait between ROT polls while SOLIDWORKS is loading.
    /// 500 ms balances responsiveness against tight-loop CPU usage.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Default maximum time to wait for SOLIDWORKS to appear in the ROT
    /// after its process has been started. SOLIDWORKS can take 30–60 s
    /// on first launch or on slower machines.
    /// </summary>
    private static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(120);

    // ------------------------------------------------------------------ //
    //  State
    // ------------------------------------------------------------------ //

    private ISldWorks? _application;
    private Process? _swProcess;
    private SwConnectionState _state = SwConnectionState.Disconnected;
    private bool _disposed;

    /// <inheritdoc/>
    public ISldWorks? Application => _application;

    /// <inheritdoc/>
    public string? RevisionNumber => _application?.RevisionNumber();

    /// <inheritdoc/>
    public SwConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc/>
    public event EventHandler<SwConnectionState>? StateChanged;

    // ------------------------------------------------------------------ //
    //  Public API
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    /// <exception cref="FileNotFoundException">
    /// Thrown when <paramref name="swExecutablePath"/> does not point to
    /// an existing file.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// Thrown when SOLIDWORKS does not register in the ROT within
    /// <see cref="DefaultLaunchTimeout"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ConnectAsync"/> is called while a connection
    /// is already active.
    /// </exception>
    public async Task ConnectAsync(
        string swExecutablePath,
        CancellationToken cancellationToken = default)
    {
        if (_state is SwConnectionState.Launching or SwConnectionState.Ready)
            throw new InvalidOperationException(
                $"Cannot connect: current state is '{_state}'. Call DisconnectAsync first.");

        if (!File.Exists(swExecutablePath))
            throw new FileNotFoundException(
                "SOLIDWORKS executable not found. Check SW_PATH in appsettings.json.",
                swExecutablePath);

        State = SwConnectionState.Launching;

        try
        {
            // Start the SOLIDWORKS process. We need the PID to build the
            // ROT moniker name ("SolidWorks_PID_<id>") that SW registers
            // once it has finished loading.
            _swProcess = Process.Start(new ProcessStartInfo
            {
                FileName = swExecutablePath,
                UseShellExecute = true   // Required for SW to initialise its COM environment correctly
            });

            if (_swProcess is null)
            {
                State = SwConnectionState.Failed;
                throw new InvalidOperationException("Process.Start returned null — SOLIDWORKS failed to launch.");
            }

            // Poll ROT on a background thread so the UI stays responsive.
            // ConnectAsync is awaited by the ViewModel, which will update
            // loading indicators via StateChanged events.
            _application = await Task.Run(
                () => WaitForSwInRot(_swProcess.Id, DefaultLaunchTimeout, cancellationToken),
                cancellationToken);

            // Make the SW window visible. When started via Process.Start
            // rather than COM activation, SW may initialise with Visible = false.
            _application.Visible = true;

            // Monitor for unexpected SW process exit (crash or user closes SW).
            _swProcess.EnableRaisingEvents = true;
            _swProcess.Exited += OnSwProcessExited;

            State = SwConnectionState.Ready;
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — e.g. user closed our app while SW was loading.
            await CleanupProcessAsync();
            State = SwConnectionState.Disconnected;
            throw;
        }
        catch
        {
            State = SwConnectionState.Failed;
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        if (_state == SwConnectionState.Disconnected) return;

        try
        {
            // ISldWorks.ExitApp() is the clean API-level shutdown.
            // Only call it if we still have a live COM reference.
            if (_application is not null)
            {
                try { _application.ExitApp(); }
                catch (COMException) { /* SW already gone — that's fine */ }

                Marshal.ReleaseComObject(_application);
                _application = null;
            }
        }
        finally
        {
            await CleanupProcessAsync();
            State = SwConnectionState.Disconnected;
        }
    }

    // ------------------------------------------------------------------ //
    //  ROT polling
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Blocks (on a background thread) until SOLIDWORKS registers its COM
    /// object in the Windows Running Object Table under the moniker
    /// <c>SolidWorks_PID_&lt;processId&gt;</c>, then returns the typed
    /// <see cref="ISldWorks"/> interface pointer.
    /// </summary>
    /// <param name="processId">
    /// The PID of the SOLIDWORKS process to wait for.
    /// </param>
    /// <param name="timeout">
    /// Maximum time to wait before throwing <see cref="TimeoutException"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token to abort the wait early if the user cancels.
    /// </param>
    /// <returns>A live <see cref="ISldWorks"/> reference.</returns>
    /// <exception cref="TimeoutException">
    /// SOLIDWORKS did not appear in the ROT within <paramref name="timeout"/>.
    /// </exception>
    private ISldWorks WaitForSwInRot(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var app = TryGetSwFromRot(processId);
            if (app is not null) return app;

            Thread.Sleep(PollingInterval);
        }

        throw new TimeoutException(
            $"SOLIDWORKS (PID {processId}) did not register in the ROT within {timeout.TotalSeconds}s. " +
            "Ensure it is not blocked by a license dialog or startup error.");
    }

    /// <summary>
    /// Performs a single query of the Windows Running Object Table (ROT)
    /// for a SOLIDWORKS instance matching the given process ID.
    /// </summary>
    /// <param name="processId">
    /// The PID to search for. SOLIDWORKS registers its COM object under
    /// the moniker <c>SolidWorks_PID_&lt;processId&gt;</c>.
    /// </param>
    /// <returns>
    /// The <see cref="ISldWorks"/> interface if found; otherwise
    /// <see langword="null"/>.
    /// </returns>
    private static ISldWorks? TryGetSwFromRot(int processId)
    {
        var targetMoniker = $"SolidWorks_PID_{processId}";

        IBindCtx? bindCtx = null;
        IRunningObjectTable? rot = null;
        IEnumMoniker? monikers = null;

        try
        {
            CreateBindCtx(0, out bindCtx);
            bindCtx.GetRunningObjectTable(out rot);
            rot.EnumRunning(out monikers);

            var buffer = new IMoniker[1];

            while (monikers.Next(1, buffer, IntPtr.Zero) == 0)
            {
                var moniker = buffer[0];
                if (moniker is null) continue;

                string? name = null;
                try { moniker.GetDisplayName(bindCtx, null, out name); }
                catch (UnauthorizedAccessException) { continue; }

                if (!string.Equals(name, targetMoniker, StringComparison.OrdinalIgnoreCase))
                    continue;

                rot.GetObject(moniker, out var obj);
                return obj as ISldWorks;
            }
        }
        finally
        {
            // COM reference counting: always release ROT objects.
            // Failing to do so leaks native memory.
            if (monikers is not null) Marshal.ReleaseComObject(monikers);
            if (rot is not null)     Marshal.ReleaseComObject(rot);
            if (bindCtx is not null) Marshal.ReleaseComObject(bindCtx);
        }

        return null;
    }

    // ------------------------------------------------------------------ //
    //  Crash / unexpected exit detection
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Handles the <see cref="Process.Exited"/> event raised when the
    /// SOLIDWORKS process terminates unexpectedly (crash or user closes SW
    /// manually). Transitions state to <see cref="SwConnectionState.Lost"/>
    /// so the UI can prompt the user to relaunch.
    /// </summary>
    private void OnSwProcessExited(object? sender, EventArgs e)
    {
        _application = null;
        State = SwConnectionState.Lost;
    }

    // ------------------------------------------------------------------ //
    //  Cleanup helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Kills the SOLIDWORKS process if it is still running and releases
    /// the managed <see cref="Process"/> handle.
    /// </summary>
    private async Task CleanupProcessAsync()
    {
        if (_swProcess is null) return;

        _swProcess.Exited -= OnSwProcessExited;

        try
        {
            if (!_swProcess.HasExited)
            {
                _swProcess.Kill();
                await _swProcess.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { /* Process already gone */ }
        finally
        {
            _swProcess.Dispose();
            _swProcess = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Fire-and-forget dispose — in practice the WPF app lifecycle
        // should call DisconnectAsync() explicitly on shutdown.
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
