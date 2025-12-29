namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// Performance metrics for the tick processor.
    /// Used for monitoring and diagnostics.
    /// </summary>
    public class TickProcessorMetrics
    {
        /// <summary>Total number of ticks queued for processing</summary>
        public long TicksQueued { get; set; }

        /// <summary>Total number of ticks successfully processed</summary>
        public long TicksProcessed { get; set; }

        /// <summary>Current number of pending ticks in queue</summary>
        public long PendingTicks { get; set; }

        /// <summary>Number of active subscriptions</summary>
        public int SubscriptionCount { get; set; }

        /// <summary>Number of symbol mappings in cache</summary>
        public int SymbolMappingCount { get; set; }

        /// <summary>Whether the processor is operating normally</summary>
        public bool IsHealthy { get; set; }

        /// <summary>Current throughput in ticks per second</summary>
        public long CurrentTicksPerSecond { get; set; }

        /// <summary>Peak throughput observed</summary>
        public long PeakTicksPerSecond { get; set; }

        /// <summary>Average time to process a tick in milliseconds</summary>
        public double AverageProcessingTimeMs { get; set; }

        /// <summary>Ratio of processed to queued ticks (0-100%)</summary>
        public double ProcessingEfficiency { get; set; }

        /// <summary>Total number of ticks dropped due to backpressure</summary>
        public long TotalTicksDropped { get; set; }
    }
}
