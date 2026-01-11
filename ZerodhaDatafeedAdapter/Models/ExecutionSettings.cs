using Newtonsoft.Json;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Execution settings for signal-based trading.
    /// Controls position sizing and risk management.
    /// </summary>
    public class ExecutionSettings
    {
        /// <summary>
        /// Maximum exposure per entry in INR.
        /// Quantity = EntryExposure / (LotSize * EntryPrice), rounded to nearest int.
        /// Example: 25000 / (10 * 500) = 5 lots
        /// </summary>
        [JsonProperty("EntryExposure")]
        public decimal EntryExposure { get; set; } = 25000m;

        /// <summary>
        /// Maximum number of lots per position regardless of exposure calculation.
        /// Acts as a hard cap on position size.
        /// </summary>
        [JsonProperty("MaxLots")]
        public int MaxLots { get; set; } = 10;

        /// <summary>
        /// Maximum stoploss exposure per position in INR.
        /// Position is closed if unrealized loss exceeds this value.
        /// Example: If StoplossExposure = 5000 and loss reaches -5000, exit immediately.
        /// </summary>
        [JsonProperty("StoplossExposure")]
        public decimal StoplossExposure { get; set; } = 5000m;

        /// <summary>
        /// Calculates the number of lots based on entry exposure.
        /// </summary>
        /// <param name="entryPrice">The entry price per unit</param>
        /// <param name="lotSize">The contract lot size</param>
        /// <returns>Number of lots, capped at MaxLots</returns>
        public int CalculateLots(decimal entryPrice, int lotSize)
        {
            if (entryPrice <= 0 || lotSize <= 0)
                return 1;

            // Quantity = Exposure / (LotSize * Price)
            decimal lots = EntryExposure / (lotSize * entryPrice);
            int roundedLots = (int)System.Math.Round(lots);

            // Ensure at least 1 lot and cap at MaxLots
            return System.Math.Max(1, System.Math.Min(roundedLots, MaxLots));
        }

        /// <summary>
        /// Checks if the unrealized loss exceeds the stoploss exposure.
        /// </summary>
        /// <param name="unrealizedPnL">Current unrealized P&L (negative for loss)</param>
        /// <returns>True if stoploss should be triggered</returns>
        public bool IsStoplossTriggered(decimal unrealizedPnL)
        {
            // Stoploss is triggered when loss exceeds StoplossExposure
            return unrealizedPnL < 0 && System.Math.Abs(unrealizedPnL) >= StoplossExposure;
        }
    }
}
