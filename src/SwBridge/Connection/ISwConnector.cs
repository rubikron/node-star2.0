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
    /// Gets the SOLIDWORKS revision string (e.g. "32.1.0") once connected,
    /// or null if not yet connected. Format is Major.Minor.Patch where Major
    /// corresponds to the SW release year (32 = 2024, 33 = 2025, etc.).
    /// </summary>
    string? RevisionNumber { get; }

    /// <summary>
    /// Gets the current connection state of the SOLIDWORKS instance.
    /// </summary>
    SwConnectionState State { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes — e.g. from Launching to Ready.
    /// The UI binds to this to update the loading indicator.
    /// </summary>
    event EventHandler<SwConnectionState> StateChanged;

    /// <summary>
    /// Spawns a new SOLIDWORKS process and waits until the application
    /// is ready to accept API calls.
    /// </summary>
    /// <param name="swExecutablePath">
    /// Full path to SLDWORKS.exe, e.g.
    /// C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe
    /// </param>
    /// <param name="cancellationToken">
    /// Token to cancel the launch — e.g. if the user closes
    /// the app while SW is still loading.
    /// </param>
    Task ConnectAsync(string swExecutablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanly shuts down the SOLIDWORKS instance that was spawned
    /// by this connector and releases all COM references.
    /// </summary>
    Task DisconnectAsync();
}
