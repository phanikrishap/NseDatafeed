using System;

namespace QANinjaAdapter.Models.MarketData
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
        /// Reset the item for object pool reuse
        /// Returns ZerodhaTickData to pool and clears all references
        /// </summary>
        public void Reset()
        {
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
