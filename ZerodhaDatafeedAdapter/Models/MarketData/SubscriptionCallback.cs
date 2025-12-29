using System;
using NinjaTrader.Cbi;
using NinjaTrader.Data;

namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// Cached subscription callback information for O(1) lookup during tick processing.
    /// Associates a NinjaTrader instrument with its market data callback.
    /// </summary>
    public class SubscriptionCallback
    {
        /// <summary>
        /// The NinjaTrader instrument receiving market data
        /// </summary>
        public Instrument Instrument { get; set; }

        /// <summary>
        /// The callback to invoke with market data updates.
        /// Parameters: MarketDataType, price, volume, timestamp, unknown
        /// </summary>
        public Action<MarketDataType, double, long, DateTime, long> Callback { get; set; }
    }
}
