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
    /// Gets the SOLIDWORKS revision string once connected,
    /// or <see langword="null"/> if not yet connected.
    /// </summary>
    string? RevisionNumber { get; }

    /// <summary>
    /// Gets the current connection state of the SOLIDWORKS instance.
    /// </summary>
    SwConnectionState State { get; }

    /// <summary>
    /// Gets whether the tracked SOLIDWORKS process is still running.
    /// </summary>
    bool HasActiveProcess { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    event EventHandler<SwConnectionState> StateChanged;

    /// <summary>
    /// Launches or attaches to SOLIDWORKS and waits for it to be ready.
    /// </summary>
    /// <param name="swExecutablePath">Path to a SOLIDWORKS launch target.</param>
    /// <param name="cancellationToken">Token used to cancel the launch operation</param>
    Task ConnectAsync(string swExecutablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the tracked SOLIDWORKS instance and releases COM references.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Releases COM references without closing SOLIDWORKS.
    /// </summary>
    Task DetachAsync();

    /// <summary>
    /// Resizes and repositions the SOLIDWORKS main window.
    /// </summary>
    /// <param name="left">The target left screen coordinate.</param>
    /// <param name="top">The target top screen coordinate.</param>
    /// <param name="width">The target window width.</param>
    /// <param name="height">The target window height.</param>
    /// <param name="cancellationToken">Token used to cancel the launch operation</param>
    Task ResizeMainWindowAsync(
        int left,
        int top,
        int width,
        int height,
        CancellationToken cancellationToken = default);
}
