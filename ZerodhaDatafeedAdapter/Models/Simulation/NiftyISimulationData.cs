using System;
using System.Collections.Generic;
using ZerodhaDatafeedAdapter.Services.Analysis;

namespace ZerodhaDatafeedAdapter.Models.Simulation
{
    /// <summary>
    /// Pre-loaded NIFTY_I data for simulation mode.
    /// Loaded by SimulationService during Loading state, consumed by NiftyFuturesMetricsService.
    /// This ensures NiftyFuturesMetricsService can start immediately when simulation begins.
    /// </summary>
    public class NiftyISimulationData
    {
        /// <summary>
        /// RangeATR bars for prior days (for building historical VP/RelMetrics).
        /// </summary>
        public List<RangeATRBar> PriorDayBars { get; set; } = new List<RangeATRBar>();

        /// <summary>
        /// Tick data for prior days (for building historical VP/RelMetrics).
        /// </summary>
        public List<HistoricalTick> PriorDayTicks { get; set; } = new List<HistoricalTick>();

        /// <summary>
        /// RangeATR bars for the simulation date itself.
        /// </summary>
        public List<RangeATRBar> SimulationDayBars { get; set; } = new List<RangeATRBar>();

        /// <summary>
        /// Tick data for the simulation date itself.
        /// </summary>
        public List<HistoricalTick> SimulationDayTicks { get; set; } = new List<HistoricalTick>();

        /// <summary>
        /// Daily bars for ADR/yearly calculations (252 bars).
        /// </summary>
        public List<DailyBar> DailyBars { get; set; } = new List<DailyBar>();

        /// <summary>
        /// The simulation date this data was loaded for.
        /// </summary>
        public DateTime SimulationDate { get; set; }

        /// <summary>
        /// True if all data was loaded successfully.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Total time taken to load all data (in milliseconds).
        /// </summary>
        public long LoadTimeMs { get; set; }

        /// <summary>
        /// Error message if loading failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        public override string ToString()
        {
            if (!IsValid)
                return $"[NiftyISimulationData] Invalid - {ErrorMessage}";

            return $"[NiftyISimulationData] {SimulationDate:yyyy-MM-dd}: " +
                   $"PriorBars={PriorDayBars.Count}, PriorTicks={PriorDayTicks.Count}, " +
                   $"SimBars={SimulationDayBars.Count}, SimTicks={SimulationDayTicks.Count}, " +
                   $"DailyBars={DailyBars.Count}, LoadTime={LoadTimeMs}ms";
        }
    }

    /// <summary>
    /// Represents a daily OHLCV bar for NIFTY_I.
    /// Used for ADR calculations and yearly high/low tracking.
    /// </summary>
    public class DailyBar
    {
        public DateTime Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }
}
