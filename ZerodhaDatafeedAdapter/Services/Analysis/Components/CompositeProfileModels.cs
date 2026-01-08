using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Stores a single trading day's session profile with volume at price data.
    /// Used for building composite profiles across multiple days.
    /// </summary>
    public class DailySessionProfile
    {
        public DateTime Date { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Open { get; set; }
        public double Close { get; set; }
        public double Range => High - Low;
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public long TotalVolume { get; set; }
        public double VWAP { get; set; }

        public Dictionary<double, (long BuyVolume, long SellVolume)> PriceLadder { get; set; }
            = new Dictionary<double, (long, long)>();

        public List<double> HVNs { get; set; } = new List<double>();
        public int HVNBuyCount { get; set; }
        public int HVNSellCount { get; set; }
        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Composite profile computed from multiple daily session profiles.
    /// </summary>
    public class CompositeProfile
    {
        public int Days { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public DateTime HighDate { get; set; }
        public DateTime LowDate { get; set; }
        public double Range => High - Low;
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public long TotalVolume { get; set; }

        public Dictionary<double, (long BuyVolume, long SellVolume)> AggregatePriceLadder { get; set; }
            = new Dictionary<double, (long, long)>();

        public List<double> HVNs { get; set; } = new List<double>();
        public int HVNBuyCount { get; set; }
        public int HVNSellCount { get; set; }
        public bool IsValid { get; set; }
    }

    /// <summary>
    /// ADR (Average Daily Range) metrics for various lookback periods.
    /// </summary>
    public class ADRMetrics
    {
        public double Range1D { get; set; }
        public double Range3D { get; set; }
        public double Range5D { get; set; }
        public double Range10D { get; set; }

        public double Avg1DADR { get; set; }
        public double Avg3DADR { get; set; }
        public double Avg5DADR { get; set; }
        public double Avg10DADR { get; set; }

        public double Range1DVsAvg => Avg1DADR > 0 ? Range1D / Avg1DADR : 0;
        public double Range3DVsAvg => Avg3DADR > 0 ? Range3D / Avg3DADR : 0;
        public double Range5DVsAvg => Avg5DADR > 0 ? Range5D / Avg5DADR : 0;
        public double Range10DVsAvg => Avg10DADR > 0 ? Range10D / Avg10DADR : 0;
    }

    /// <summary>
    /// Rolling range metrics - composite range that includes current session.
    /// </summary>
    public class RollingRangeMetrics
    {
        public double Rolling3DRange { get; set; }
        public double Rolling5DRange { get; set; }
        public double Rolling10DRange { get; set; }
        public double Rolling3DHigh { get; set; }
        public double Rolling3DLow { get; set; }
        public double Rolling5DHigh { get; set; }
        public double Rolling5DLow { get; set; }
        public double Rolling10DHigh { get; set; }
        public double Rolling10DLow { get; set; }
        public double Rolling3DVsAvg { get; set; }
        public double Rolling5DVsAvg { get; set; }
        public double Rolling10DVsAvg { get; set; }
    }

    /// <summary>
    /// Yearly high/low tracking with dates.
    /// </summary>
    public class YearlyExtremes
    {
        public double YearlyHigh { get; set; }
        public DateTime YearlyHighDate { get; set; }
        public double YearlyLow { get; set; }
        public DateTime YearlyLowDate { get; set; }
        public double YearlyRange => YearlyHigh - YearlyLow;
        public double PositionInRange { get; set; }
    }

    /// <summary>
    /// Complete composite profile metrics snapshot for UI display.
    /// Mirrors the FutBias "COMPOSITE PROFILE METRICS" table structure.
    /// </summary>
    public class CompositeProfileMetrics
    {
        // ═══════════════════════════════════════════════════════════════════
        // COMPOSITE PROFILE LEVELS (from 1D, 3D, 5D, 10D)
        // ═══════════════════════════════════════════════════════════════════

        public double POC_1D { get; set; }
        public double POC_3D { get; set; }
        public double POC_5D { get; set; }
        public double POC_10D { get; set; }
        public double VAH_1D { get; set; }
        public double VAH_3D { get; set; }
        public double VAH_5D { get; set; }
        public double VAH_10D { get; set; }
        public double VAL_1D { get; set; }
        public double VAL_3D { get; set; }
        public double VAL_5D { get; set; }
        public double VAL_10D { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // ADR AND RANGE METRICS
        // ═══════════════════════════════════════════════════════════════════

        public ADRMetrics ADR { get; set; } = new ADRMetrics();
        public RollingRangeMetrics RollingRange { get; set; } = new RollingRangeMetrics();
        public YearlyExtremes YearlyExtremes { get; set; } = new YearlyExtremes();

        // ═══════════════════════════════════════════════════════════════════
        // COMPOSITE RANGE ROW (Comp Rng)
        // ═══════════════════════════════════════════════════════════════════

        public double CompRange_1D { get; set; }
        public double CompRange_3D { get; set; }
        public double CompRange_5D { get; set; }
        public double CompRange_10D { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // C VS AVG ROW (Current range vs average)
        // ═══════════════════════════════════════════════════════════════════

        public double CVsAvg_1D { get; set; }
        public double CVsAvg_3D { get; set; }
        public double CVsAvg_5D { get; set; }
        public double CVsAvg_10D { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // ROLL RNG ROW (Rolling range including today)
        // ═══════════════════════════════════════════════════════════════════

        public double RollRange_1D { get; set; }
        public double RollRange_3D { get; set; }
        public double RollRange_5D { get; set; }
        public double RollRange_10D { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // R VS AVG ROW (Rolling range vs average)
        // ═══════════════════════════════════════════════════════════════════

        public double RVsAvg_1D { get; set; }
        public double RVsAvg_3D { get; set; }
        public double RVsAvg_5D { get; set; }
        public double RVsAvg_10D { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // PRIOR EOD (D-2, D-3, D-4 ranges for each period)
        // D-2 Rng = N-day composite range ending 2 days ago
        // D-2 % = that range vs average N-day range
        // ═══════════════════════════════════════════════════════════════════

        // D-2 (day before yesterday) - N-day ranges ending on D-2
        public double D2_1DRange { get; set; }
        public double D2_3DRange { get; set; }
        public double D2_5DRange { get; set; }
        public double D2_10DRange { get; set; }
        public double D2_1DVsAvg { get; set; }
        public double D2_3DVsAvg { get; set; }
        public double D2_5DVsAvg { get; set; }
        public double D2_10DVsAvg { get; set; }

        // D-3 (3 days ago) - N-day ranges ending on D-3
        public double D3_1DRange { get; set; }
        public double D3_3DRange { get; set; }
        public double D3_5DRange { get; set; }
        public double D3_10DRange { get; set; }
        public double D3_1DVsAvg { get; set; }
        public double D3_3DVsAvg { get; set; }
        public double D3_5DVsAvg { get; set; }
        public double D3_10DVsAvg { get; set; }

        // D-4 (4 days ago) - N-day ranges ending on D-4
        public double D4_1DRange { get; set; }
        public double D4_3DRange { get; set; }
        public double D4_5DRange { get; set; }
        public double D4_10DRange { get; set; }
        public double D4_1DVsAvg { get; set; }
        public double D4_3DVsAvg { get; set; }
        public double D4_5DVsAvg { get; set; }
        public double D4_10DVsAvg { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // CONTROL AND MIGRATION
        // ═══════════════════════════════════════════════════════════════════

        public string Control { get; set; }
        public string Migration { get; set; }

        // ═══════════════════════════════════════════════════════════════════
        // METADATA
        // ═══════════════════════════════════════════════════════════════════

        public double CurrentPrice { get; set; }
        public DateTime LastUpdate { get; set; }
        public string Symbol { get; set; }
        public bool IsValid { get; set; }
        public int DailyBarCount { get; set; }
    }
}
