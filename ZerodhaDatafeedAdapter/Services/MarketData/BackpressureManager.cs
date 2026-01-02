using System;
using System.Threading;
using ZerodhaDatafeedAdapter.Models.MarketData;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Manages tiered backpressure for the tick processor.
    /// Provides intelligent tick dropping based on queue depth and symbol priority.
    /// Extracted from OptimizedTickProcessor for better separation of concerns.
    /// </summary>
    public class BackpressureManager
    {
        #region Configuration

        private readonly int _warningQueueSize;
        private readonly int _criticalQueueSize;
        private readonly int _maxAcceptableQueueSize;
        private readonly int _maxQueueSize;

        #endregion

        #region State

        private BackpressureState _currentState = BackpressureState.Normal;
        private DateTime _lastStateChangeTime = DateTime.MinValue;

        // Counters
        private long _ticksDroppedBackpressure = 0;
        private long _warningLevelEvents = 0;
        private long _criticalLevelEvents = 0;

        #endregion

        #region Properties

        /// <summary>
        /// Current backpressure state
        /// </summary>
        public BackpressureState CurrentState => _currentState;

        /// <summary>
        /// Time of last state change
        /// </summary>
        public DateTime LastStateChangeTime => _lastStateChangeTime;

        /// <summary>
        /// Total ticks dropped due to backpressure
        /// </summary>
        public long TicksDroppedBackpressure => Interlocked.Read(ref _ticksDroppedBackpressure);

        /// <summary>
        /// Count of warning level events
        /// </summary>
        public long WarningLevelEvents => Interlocked.Read(ref _warningLevelEvents);

        /// <summary>
        /// Count of critical level events
        /// </summary>
        public long CriticalLevelEvents => Interlocked.Read(ref _criticalLevelEvents);

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize backpressure manager with queue size limits
        /// </summary>
        /// <param name="maxQueueSize">Maximum queue size</param>
        public BackpressureManager(int maxQueueSize)
        {
            _maxQueueSize = maxQueueSize;
            _warningQueueSize = (int)(maxQueueSize * 0.6);
            _criticalQueueSize = (int)(maxQueueSize * 0.8);
            _maxAcceptableQueueSize = (int)(maxQueueSize * 0.9);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Evaluate whether a tick should be queued based on current backpressure state.
        /// Returns true if the tick should be queued, false if it should be dropped.
        /// </summary>
        /// <param name="currentQueueDepth">Current queue depth</param>
        /// <param name="symbol">Symbol name for priority evaluation</param>
        /// <returns>Tuple of (shouldQueue, reason)</returns>
        public (bool shouldQueue, string reason) EvaluateTick(long currentQueueDepth, string symbol)
        {
            // Update backpressure state
            var newState = DetermineBackpressureState(currentQueueDepth);
            if (newState != _currentState)
            {
                var previousState = _currentState;
                _currentState = newState;
                _lastStateChangeTime = DateTime.UtcNow;

                Logger.Info($"[Backpressure] State change: {previousState} -> {newState} (Queue: {currentQueueDepth})");

                // Increment event counters
                if (newState == BackpressureState.Warning) Interlocked.Increment(ref _warningLevelEvents);
                if (newState == BackpressureState.Critical) Interlocked.Increment(ref _criticalLevelEvents);
            }

            switch (_currentState)
            {
                case BackpressureState.Normal:
                    return (true, "Normal processing");

                case BackpressureState.Warning:
                    // At warning level: Apply selective dropping for low-priority symbols
                    if (IsLowPrioritySymbol(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Dropped low-priority symbol {symbol} at warning level");
                    }
                    return (true, "Warning level - high priority symbols only");

                case BackpressureState.Critical:
                    // At critical level: Apply aggressive dropping and oldest tick removal
                    if (ShouldDropTickAtCriticalLevel(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Dropped symbol {symbol} at critical level");
                    }
                    return (true, "Critical level - accepting high priority symbol");

                case BackpressureState.Emergency:
                    // At emergency level: Drop all but essential ticks
                    if (!IsEssentialSymbol(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Emergency: Only essential symbols accepted, dropped {symbol}");
                    }
                    return (true, "Emergency level - essential symbol accepted");

                default:
                    // Absolute maximum: reject everything
                    Interlocked.Increment(ref _ticksDroppedBackpressure);
                    return (false, $"Queue at maximum capacity ({currentQueueDepth}), rejected {symbol}");
            }
        }

        /// <summary>
        /// Record a tick drop (for external tracking)
        /// </summary>
        public void RecordTickDropped()
        {
            Interlocked.Increment(ref _ticksDroppedBackpressure);
        }

        /// <summary>
        /// Get current metrics for health monitoring
        /// </summary>
        public BackpressureMetrics GetMetrics()
        {
            return new BackpressureMetrics
            {
                CurrentState = _currentState,
                TicksDropped = Interlocked.Read(ref _ticksDroppedBackpressure),
                WarningEvents = Interlocked.Read(ref _warningLevelEvents),
                CriticalEvents = Interlocked.Read(ref _criticalLevelEvents),
                LastStateChangeTime = _lastStateChangeTime
            };
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Determine backpressure state based on queue depth
        /// </summary>
        private BackpressureState DetermineBackpressureState(long queueDepth)
        {
            if (queueDepth >= _maxQueueSize) return BackpressureState.Maximum;
            if (queueDepth >= _maxAcceptableQueueSize) return BackpressureState.Emergency;
            if (queueDepth >= _criticalQueueSize) return BackpressureState.Critical;
            if (queueDepth >= _warningQueueSize) return BackpressureState.Warning;
            return BackpressureState.Normal;
        }

        /// <summary>
        /// Determine if symbol is low priority for dropping
        /// </summary>
        private bool IsLowPrioritySymbol(string symbol)
        {
            return string.IsNullOrEmpty(symbol) ||
                   symbol.Contains("TEST") ||
                   symbol.Contains("DEMO");
        }

        /// <summary>
        /// Determine if tick should be dropped at critical level
        /// </summary>
        private bool ShouldDropTickAtCriticalLevel(string symbol)
        {
            // At critical level, be more aggressive - drop 30% of non-essential ticks
            return !IsEssentialSymbol(symbol) && (symbol.GetHashCode() % 10 < 3);
        }

        /// <summary>
        /// Determine if symbol is essential and should never be dropped
        /// </summary>
        private bool IsEssentialSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            // Major indices and high-volume stocks are essential
            return symbol.Contains("NIFTY") ||
                   symbol.Contains("SENSEX") ||
                   symbol.Contains("BANKNIFTY");
        }

        #endregion
    }

    /// <summary>
    /// Metrics snapshot for backpressure state
    /// </summary>
    public class BackpressureMetrics
    {
        public BackpressureState CurrentState { get; set; }
        public long TicksDropped { get; set; }
        public long WarningEvents { get; set; }
        public long CriticalEvents { get; set; }
        public DateTime LastStateChangeTime { get; set; }
    }
}
