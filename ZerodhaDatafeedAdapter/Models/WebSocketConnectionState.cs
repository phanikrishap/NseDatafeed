namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Connection state machine for WebSocket services.
    /// Provides atomic state transitions to prevent race conditions.
    /// </summary>
    public enum WebSocketConnectionState
    {
        /// <summary>Initial state - never connected</summary>
        Disconnected = 0,

        /// <summary>Connection attempt in progress</summary>
        Connecting = 1,

        /// <summary>Connected and ready for operations</summary>
        Connected = 2,

        /// <summary>Reconnecting after disconnect</summary>
        Reconnecting = 3,

        /// <summary>Shutting down - reject all operations</summary>
        Disposing = 4
    }
}
