using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents the current phase of adapter initialization.
    /// </summary>
    public enum InitializationPhase
    {
        /// <summary>Not started yet</summary>
        NotStarted,

        /// <summary>Token validation in progress</summary>
        ValidatingToken,

        /// <summary>Token validation completed (check IsTokenValid for result)</summary>
        TokenValidated,

        /// <summary>Checking if instrument database needs refresh</summary>
        CheckingInstrumentDb,

        /// <summary>Downloading instruments from Zerodha API</summary>
        DownloadingInstruments,

        /// <summary>Loading instruments into memory cache</summary>
        LoadingInstrumentCache,

        /// <summary>Initialization completed successfully - ready for subscriptions</summary>
        Ready,

        /// <summary>Initialization failed - see ErrorMessage for details</summary>
        Failed
    }

    /// <summary>
    /// Represents the current initialization state of the adapter.
    /// Published via MarketDataReactiveHub.InitializationStateStream.
    /// Subscribers can await specific phases or the Ready state before proceeding.
    /// </summary>
    public class InitializationState
    {
        /// <summary>
        /// Current phase of initialization.
        /// </summary>
        public InitializationPhase Phase { get; set; }

        /// <summary>
        /// True if token validation succeeded.
        /// Only meaningful when Phase >= TokenValidated.
        /// </summary>
        public bool IsTokenValid { get; set; }

        /// <summary>
        /// True if instrument database is ready for lookups.
        /// Only meaningful when Phase >= Ready.
        /// </summary>
        public bool IsInstrumentDbReady { get; set; }

        /// <summary>
        /// Progress percentage (0-100) for the current phase.
        /// Useful for UI progress indicators.
        /// </summary>
        public int ProgressPercent { get; set; }

        /// <summary>
        /// Human-readable status message for the current phase.
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// Error message if Phase == Failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Number of retry attempts made (for download phase).
        /// </summary>
        public int RetryAttempt { get; set; }

        /// <summary>
        /// Maximum retry attempts configured.
        /// </summary>
        public int MaxRetries { get; set; }

        /// <summary>
        /// Timestamp when this state was published.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// True if initialization has completed (either Ready or Failed).
        /// </summary>
        public bool IsComplete => Phase == InitializationPhase.Ready || Phase == InitializationPhase.Failed;

        /// <summary>
        /// True if initialization completed successfully.
        /// </summary>
        public bool IsReady => Phase == InitializationPhase.Ready;

        /// <summary>
        /// Creates an initial "not started" state.
        /// </summary>
        public static InitializationState NotStarted => new InitializationState
        {
            Phase = InitializationPhase.NotStarted,
            StatusMessage = "Initialization not started",
            Timestamp = DateTime.Now
        };

        /// <summary>
        /// Creates a state for a specific phase with message.
        /// </summary>
        public static InitializationState ForPhase(InitializationPhase phase, string message, int progressPercent = 0)
        {
            return new InitializationState
            {
                Phase = phase,
                StatusMessage = message,
                ProgressPercent = progressPercent,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a failed state with error message.
        /// </summary>
        public static InitializationState Fail(string errorMessage)
        {
            return new InitializationState
            {
                Phase = InitializationPhase.Failed,
                StatusMessage = "Initialization failed",
                ErrorMessage = errorMessage,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a ready state.
        /// </summary>
        public static InitializationState Ready(bool tokenValid, bool dbReady)
        {
            return new InitializationState
            {
                Phase = InitializationPhase.Ready,
                IsTokenValid = tokenValid,
                IsInstrumentDbReady = dbReady,
                ProgressPercent = 100,
                StatusMessage = "Initialization complete - ready for subscriptions",
                Timestamp = DateTime.Now
            };
        }

        public override string ToString()
        {
            return $"[{Phase}] {StatusMessage} ({ProgressPercent}%)";
        }
    }
}
