using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents a synthetic straddle price update.
    /// Straddle price = CE price + PE price for the same strike.
    /// </summary>
    public class StraddlePriceUpdate
    {
        /// <summary>
        /// Straddle symbol (e.g., NIFTY25JAN23500_STRDL)
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Combined straddle price (CE + PE)
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// CE component price
        /// </summary>
        public double CEPrice { get; set; }

        /// <summary>
        /// PE component price
        /// </summary>
        public double PEPrice { get; set; }

        /// <summary>
        /// Strike price
        /// </summary>
        public double Strike { get; set; }

        /// <summary>
        /// Timestamp when this was calculated
        /// </summary>
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            return $"{Symbol}: {Price:F2} (CE={CEPrice:F2}, PE={PEPrice:F2})";
        }
    }
}
