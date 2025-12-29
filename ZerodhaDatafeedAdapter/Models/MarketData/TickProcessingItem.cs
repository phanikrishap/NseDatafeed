using System;
using System.Threading;

namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// Item for tick processing queue - supports object pooling for reduced GC pressure
    /// </summary>
    public class TickProcessingItem
    {
        /// <summary>
        /// The native symbol name (e.g., "RELIANCE_NSE")
        /// </summary>
        public string NativeSymbolName { get; set; }

        /// <summary>
        /// The parsed tick data from Zerodha WebSocket
        /// </summary>
        public ZerodhaTickData TickData { get; set; }

        /// <summary>
        /// The time when this item was queued for processing
        /// Used for latency tracking and backpressure management
        /// </summary>
        public DateTime QueueTime { get; set; }

        /// <summary>
        /// Ready flag for producer-consumer synchronization.
        /// Producer sets to 1 after writing data, consumer sets to 0 after reading.
        /// </summary>
        private int _isReady;

        /// <summary>
        /// Check if the item is ready for processing
        /// </summary>
        public bool IsReady => Volatile.Read(ref _isReady) == 1;

        /// <summary>
        /// Mark the item as ready for processing (called by producer after writing)
        /// </summary>
        public void MarkReady()
        {
            Volatile.Write(ref _isReady, 1);
        }

        /// <summary>
        /// Reset the item for object pool reuse
        /// Returns ZerodhaTickData to pool and clears all references
        /// </summary>
        public void Reset()
        {
            // Clear ready flag first
            Volatile.Write(ref _isReady, 0);

            // Return tick data to pool before clearing reference
            if (TickData != null)
            {
                ZerodhaTickDataPool.Return(TickData);
            }

            NativeSymbolName = null;
            TickData = null;
            QueueTime = default(DateTime);
        }
    }
}
