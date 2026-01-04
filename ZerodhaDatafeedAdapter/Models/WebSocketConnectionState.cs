namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Connection state machine for WebSocket services.
    /// Provides atomic state transitions to prevent race conditions.
    ///
    /// State Diagram:
    /// ┌──────────────┐     TokenReady      ┌────────────────┐
    /// │ Disconnected │ ──────────────────► │   Connecting   │
    /// └──────────────┘                     └────────────────┘
    ///        ▲                                    │
    ///        │                              Success│ Failure
    ///        │                                    ▼    │
    ///        │                             ┌──────────┐│
    ///        │◄────────── Dispose ─────────│Connected ││
    ///        │                             └──────────┘│
    ///        │                                  │      │
    ///        │                           Error  │      │
    ///        │                                  ▼      ▼
    ///        │                             ┌────────────────┐
    ///        │◄──── MaxRetries ────────────│   BackingOff   │
    ///        │                             └────────────────┘
    ///        │                                    │
    ///        │                              Timer │
    ///        │                                    ▼
    ///        │                             ┌────────────────┐
    ///        └─────────────────────────────│  Reconnecting  │
    ///                                      └────────────────┘
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
        Disposing = 4,

        /// <summary>
        /// Waiting before retry with exponential backoff.
        /// Delays: 1s → 2s → 4s → 8s → 16s (max)
        /// Transitions to Reconnecting when timer expires.
        /// </summary>
        BackingOff = 5
    }
}
