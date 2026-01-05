using System;

namespace ZerodhaDatafeedAdapter.Models.Reactive
{
    /// <summary>
    /// Represents a VWAP update for an instrument.
    /// Includes VWAP value and standard deviation bands.
    /// </summary>
    public class VWAPUpdate
    {
        /// <summary>
        /// Instrument symbol
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Volume-Weighted Average Price
        /// </summary>
        public double VWAP { get; set; }

        /// <summary>
        /// VWAP + 1 Standard Deviation
        /// </summary>
        public double SD1Upper { get; set; }

        /// <summary>
        /// VWAP - 1 Standard Deviation
        /// </summary>
        public double SD1Lower { get; set; }

        /// <summary>
        /// VWAP + 2 Standard Deviations
        /// </summary>
        public double SD2Upper { get; set; }

        /// <summary>
        /// VWAP - 2 Standard Deviations
        /// </summary>
        public double SD2Lower { get; set; }

        /// <summary>
        /// Cumulative volume used in VWAP calculation
        /// </summary>
        public double CumulativeVolume { get; set; }

        /// <summary>
        /// Timestamp when this update was calculated
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Returns position relative to VWAP:
        /// +2 = above SD2Upper, +1 = above SD1Upper, 0 = between bands, -1 = below SD1Lower, -2 = below SD2Lower
        /// </summary>
        public int GetPosition(double price)
        {
            if (price >= SD2Upper) return 2;
            if (price >= SD1Upper) return 1;
            if (price <= SD2Lower) return -2;
            if (price <= SD1Lower) return -1;
            return 0;
        }

        /// <summary>
        /// Distance from VWAP as percentage
        /// </summary>
        public double GetDistanceFromVWAP(double price)
        {
            if (VWAP == 0) return 0;
            return ((price - VWAP) / VWAP) * 100;
        }

        public override string ToString()
        {
            return $"{Symbol}: VWAP={VWAP:F2}, SD1=[{SD1Lower:F2}-{SD1Upper:F2}]";
        }
    }
}
