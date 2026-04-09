using SolidWorks.Interop.sldworks;

namespace SwBridge.Connection;

/// <summary>
/// Defines the contract for establishing and managing a connection
/// to a SOLIDWORKS application instance.
/// </summary>
public interface ISwConnector
{
    /// <summary>
    /// Gets the active SOLIDWORKS application object.
    /// Null if <see cref="ConnectAsync"/> has not completed successfully.
    /// </summary>
    ISldWorks? Application { get; }

    /// <summary>
    /// Gets the SOLIDWORKS revision string (for example "32.1.0") once connected,
    /// or <see langword="null"/> if not yet connected.
    /// </summary>
    string? RevisionNumber { get; }

    /// <summary>
    /// Gets the current connection state of the SOLIDWORKS instance.
    /// </summary>
    SwConnectionState State { get; }

    /// <summary>
    /// Gets a value indicating whether the spawned SOLIDWORKS process is still running.
    /// </summary>
    bool HasActiveProcess { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    event EventHandler<SwConnectionState> StateChanged;

    /// <summary>
    /// Spawns a new SOLIDWORKS process and waits until the application
    /// is ready to accept API calls.
    /// </summary>
    /// <param name="swExecutablePath">
    /// Full path to <c>SLDWORKS.exe</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel the launch operation.
    /// </param>
    Task ConnectAsync(string swExecutablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanly shuts down the SOLIDWORKS instance that was spawned
    /// by this connector and releases all COM references.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Releases this application's handles and COM references without closing
    /// the spawned SOLIDWORKS process.
    /// </summary>
    Task DetachAsync();

    /// <summary>
    /// Resizes and repositions the SOLIDWORKS main window.
    /// </summary>
    /// <param name="left">The target left screen coordinate.</param>
    /// <param name="top">The target top screen coordinate.</param>
    /// <param name="width">The target window width.</param>
    /// <param name="height">The target window height.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel the window lookup and move operation.
    /// </param>
    Task ResizeMainWindowAsync(
        int left,
        int top,
        int width,
        int height,
        CancellationToken cancellationToken = default);
}
