using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// RangeATR bar data container.
    /// </summary>
    public class RangeATRBar
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>
    /// Historical tick data container.
    /// </summary>
    public class HistoricalTick
    {
        public int Index { get; set; }
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
    }

    /// <summary>
    /// Live tick data from WebSocket.
    /// </summary>
    public class LiveTick
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
    }

    /// <summary>
    /// Nifty Futures VP metrics snapshot with HVN Buy/Sell and Relative/Cumulative metrics.
    /// Includes both Session and Rolling (60-min) Volume Profile metrics.
    /// </summary>
    public class NiftyFuturesVPMetrics
    {
        // ═══════════════════════════════════════════════════════════════════
        // SESSION VP METRICS
        // ═══════════════════════════════════════════════════════════════════

        // Core Session VP metrics
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public double ValueWidth => VAH - VAL;
        public List<double> HVNs { get; set; } = new List<double>();

        // Session HVN Buy/Sell breakdown
        public int HVNBuyCount { get; set; }      // HVNs at/below close (support)
        public int HVNSellCount { get; set; }     // HVNs above close (resistance)
        public long HVNBuyVolume { get; set; }    // Total volume at buy HVNs
        public long HVNSellVolume { get; set; }   // Total volume at sell HVNs

        // Session Relative metrics (current vs historical time-of-day average * 100)
        public double RelHVNBuy { get; set; }     // RelHVNBuySess
        public double RelHVNSell { get; set; }    // RelHVNSellSess
        public double RelValueWidth { get; set; } // RelValWidthSess

        // Session Cumulative metrics (session total vs session reference total * 100)
        public double CumHVNBuyRank { get; set; }     // CumHvnBuySessRank
        public double CumHVNSellRank { get; set; }    // CumHvnSellSessRank
        public double CumValueWidthRank { get; set; } // CumValWidthSessRank

        // ═══════════════════════════════════════════════════════════════════
        // ROLLING VP METRICS (60-minute window)
        // ═══════════════════════════════════════════════════════════════════

        // Core Rolling VP metrics
        public double RollingPOC { get; set; }
        public double RollingVAH { get; set; }
        public double RollingVAL { get; set; }
        public double RollingValueWidth => RollingVAH - RollingVAL;

        // Rolling HVN Buy/Sell breakdown
        public int RollingHVNBuyCount { get; set; }   // HvnBuyRolling
        public int RollingHVNSellCount { get; set; }  // HvnSellRolling

        // Rolling Relative metrics
        public double RelHVNBuyRolling { get; set; }     // RelHvnBuyRolling
        public double RelHVNSellRolling { get; set; }    // RelHvnSellRolling
        public double RelValueWidthRolling { get; set; } // RelValWidthRolling

        // Rolling Cumulative metrics
        public double CumHVNBuyRollingRank { get; set; }     // CumHvnBuyRollingRank
        public double CumHVNSellRollingRank { get; set; }    // CumHvnSellRollingRank
        public double CumValueWidthRollingRank { get; set; } // CumValWidthRollingRank

        // ═══════════════════════════════════════════════════════════════════
        // METADATA
        // ═══════════════════════════════════════════════════════════════════

        public int BarCount { get; set; }
        public DateTime LastBarTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public string Symbol { get; set; }
        public bool IsValid { get; set; }
    }
}
