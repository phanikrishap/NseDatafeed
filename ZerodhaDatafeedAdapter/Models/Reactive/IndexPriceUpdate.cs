using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents a price update for an index instrument (GIFT_NIFTY, NIFTY, SENSEX, NIFTY_I).
    /// Used as the event data for reactive streams.
    /// </summary>
    public class IndexPriceUpdate
    {
        /// <summary>
        /// Normalized symbol name (e.g., "GIFT NIFTY", "NIFTY 50", "SENSEX", "NIFTY_I")
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Current/Last traded price
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// Prior day's closing price
        /// </summary>
        public double Close { get; set; }

        /// <summary>
        /// Net change percentage: ((Price - Close) / Close) * 100
        /// </summary>
        public double NetChangePercent { get; set; }

        /// <summary>
        /// Timestamp when this update was received
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Whether this update includes a valid close price
        /// </summary>
        public bool HasClose => Close > 0;

        /// <summary>
        /// Whether this update includes a valid current price
        /// </summary>
        public bool HasPrice => Price > 0;

        public override string ToString()
        {
            return $"{Symbol}: Price={Price:F2}, Close={Close:F2}, Change={NetChangePercent:F2}%";
        }
    }
}
