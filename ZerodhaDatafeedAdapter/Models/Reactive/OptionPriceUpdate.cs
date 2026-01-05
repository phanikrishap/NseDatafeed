using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents a price update for an option instrument.
    /// Used as the event data for reactive option price streams.
    /// </summary>
    public class OptionPriceUpdate
    {
        /// <summary>
        /// Option symbol (e.g., NIFTY25JAN23500CE)
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Current/Last traded price
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// Trading volume (if available)
        /// </summary>
        public double Volume { get; set; }

        /// <summary>
        /// Timestamp when this update was received
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Source of the update (WebSocket, Historical, Simulated)
        /// </summary>
        public string Source { get; set; }

        public override string ToString()
        {
            return $"{Symbol}: {Price:F2} @ {Timestamp:HH:mm:ss}";
        }
    }
}
