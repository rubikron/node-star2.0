namespace SwBridge.Connection;

/// <summary>
/// Represents the lifecycle state of the SOLIDWORKS connection,
/// used by the UI to display appropriate status indicators.
/// </summary>
public enum SwConnectionState
{
    /// <summary>No connection has been attempted yet.</summary>
    Disconnected,

    /// <summary>SLDWORKS.exe process has been started and is loading.</summary>
    Launching,

    /// <summary>SW is running and the API is ready to accept calls.</summary>
    Ready,

    /// <summary>The connection was lost unexpectedly — SW may have crashed.</summary>
    Lost,

    /// <summary>A fatal error occurred during launch or connection.</summary>
    Failed
}
