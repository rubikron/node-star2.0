using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidWorks.Interop.sldworks;

namespace SwBridge.Connection;

/// <summary>
/// Manages the lifecycle of a SOLIDWORKS application instance spawned
/// by this application and can also resize the spawned top-level window.
/// </summary>
/// <remarks>
/// Both this application and the SOLIDWORKS process must run under the
/// same Windows user account. Mismatched elevation levels can cause
/// Running Object Table lookup to fail.
/// </remarks>
public sealed class SwConnector : ISwConnector, IDisposable
{
    /// <summary>
    /// Creates a COM binding context, which is required as an argument
    /// to <see cref="IRunningObjectTable"/> queries.
    /// </summary>
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    /// <summary>
    /// Restores a top-level window to its normal state before resizing it.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Moves and resizes a top-level window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowHandlePollingInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultWindowHandleTimeout = TimeSpan.FromSeconds(10);

    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc/>
    public bool HasActiveProcess
    {
        get
        {
            if (_swProcess is null)
            {
                return false;
            }

            try
            {
                return !_swProcess.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<SwConnectionState>? StateChanged;

    /// <inheritdoc/>
    public async Task ConnectAsync(
        string swExecutablePath,
        CancellationToken cancellationToken = default)
    {
        if (_state is SwConnectionState.Launching or SwConnectionState.Ready)
        {
            throw new InvalidOperationException(
                $"Cannot connect: current state is '{_state}'. Call DisconnectAsync first.");
        }

        if (!File.Exists(swExecutablePath))
        {
            throw new FileNotFoundException(
                "SOLIDWORKS executable not found. Check the configured path.",
                swExecutablePath);
        }

        State = SwConnectionState.Launching;

        try
        {
            _swProcess = Process.Start(new ProcessStartInfo
            {
                FileName = swExecutablePath,
                UseShellExecute = true
            });

            if (_swProcess is null)
            {
                State = SwConnectionState.Failed;
                throw new InvalidOperationException("Process.Start returned null. SOLIDWORKS failed to launch.");
            }

            _application = await Task.Run(
                () => WaitForSwInRot(_swProcess.Id, DefaultLaunchTimeout, cancellationToken),
                cancellationToken);

            _application.Visible = true;

            _swProcess.EnableRaisingEvents = true;
            _swProcess.Exited += OnSwProcessExited;

            State = SwConnectionState.Ready;
        }
        catch (OperationCanceledException)
        {
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
        if (_state == SwConnectionState.Disconnected)
        {
            return;
        }

        try
        {
            if (_application is not null)
            {
                try
                {
                    _application.ExitApp();
                }
                catch (COMException)
                {
                    // SOLIDWORKS may already be gone.
                }

                ReleaseApplicationReference();
            }
        }
        finally
        {
            await CleanupProcessAsync();
            State = SwConnectionState.Disconnected;
        }
    }

    /// <inheritdoc/>
    public Task DetachAsync()
    {
        ReleaseApplicationReference();

        if (_swProcess is not null)
        {
            _swProcess.Exited -= OnSwProcessExited;
            _swProcess.Dispose();
            _swProcess = null;
        }

        State = SwConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ResizeMainWindowAsync(
        int left,
        int top,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (!HasActiveProcess)
        {
            throw new InvalidOperationException("Cannot resize SOLIDWORKS because no active process is available.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window bounds must be positive.");
        }

        var windowHandle = await WaitForMainWindowHandleAsync(cancellationToken);
        ShowWindowAsync(windowHandle, SwRestore);

        if (!SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to resize the SOLIDWORKS window.");
        }
    }

    /// <summary>
    /// Blocks until SOLIDWORKS registers its COM object in the Windows
    /// Running Object Table under the matching process moniker.
    /// </summary>
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
            if (app is not null)
            {
                return app;
            }

            Thread.Sleep(PollingInterval);
        }

        throw new TimeoutException(
            $"SOLIDWORKS (PID {processId}) did not register in the ROT within {timeout.TotalSeconds}s.");
    }

    /// <summary>
    /// Performs a single Running Object Table query for the specified process ID.
    /// </summary>
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
            if (rot is null)
            {
                return null;
            }

            rot.EnumRunning(out monikers);
            if (monikers is null)
            {
                return null;
            }

            var buffer = new IMoniker[1];

            while (monikers.Next(1, buffer, IntPtr.Zero) == 0)
            {
                var moniker = buffer[0];
                if (moniker is null)
                {
                    continue;
                }

                string? name = null;
                try
                {
                    moniker.GetDisplayName(bindCtx, null, out name);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (!string.Equals(name, targetMoniker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rot.GetObject(moniker, out var obj);
                return obj as ISldWorks;
            }
        }
        finally
        {
            if (monikers is not null)
            {
                Marshal.ReleaseComObject(monikers);
            }

            if (rot is not null)
            {
                Marshal.ReleaseComObject(rot);
            }

            if (bindCtx is not null)
            {
                Marshal.ReleaseComObject(bindCtx);
            }
        }

        return null;
    }

    /// <summary>
    /// Updates connector state when the spawned SOLIDWORKS process exits unexpectedly.
    /// </summary>
    private void OnSwProcessExited(object? sender, EventArgs e)
    {
        ReleaseApplicationReference();
        State = SwConnectionState.Lost;
    }

    /// <summary>
    /// Kills the spawned SOLIDWORKS process if it is still running and releases
    /// the managed <see cref="Process"/> handle.
    /// </summary>
    private async Task CleanupProcessAsync()
    {
        if (_swProcess is null)
        {
            return;
        }

        _swProcess.Exited -= OnSwProcessExited;

        try
        {
            if (!_swProcess.HasExited)
            {
                _swProcess.Kill();
                await _swProcess.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // The process may already be gone.
        }
        finally
        {
            _swProcess.Dispose();
            _swProcess = null;
        }
    }

    /// <summary>
    /// Waits for SOLIDWORKS to expose a main window handle.
    /// </summary>
    private async Task<IntPtr> WaitForMainWindowHandleAsync(CancellationToken cancellationToken)
    {
        if (_swProcess is null)
        {
            throw new InvalidOperationException("SOLIDWORKS process handle is not available.");
        }

        var deadline = DateTime.UtcNow + DefaultWindowHandleTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _swProcess.Refresh();

            if (_swProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "SOLIDWORKS exited before exposing a main window handle.");
            }

            var windowHandle = _swProcess.MainWindowHandle;
            if (windowHandle != IntPtr.Zero)
            {
                return windowHandle;
            }

            await Task.Delay(WindowHandlePollingInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            "Timed out while waiting for the SOLIDWORKS window to become available.");
    }

    /// <summary>
    /// Releases the live COM application reference if one exists.
    /// </summary>
    private void ReleaseApplicationReference()
    {
        if (_application is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(_application);
        }
        catch (COMException)
        {
            // The COM server may already be gone.
        }
        finally
        {
            _application = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
