using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    /// <summary>
    /// Pre-computed bar data for simulation playback.
    /// Stores both the row state and raw values needed for CSV writing.
    /// </summary>
    public class SimBarData
    {
        public DateTime BarTime { get; set; }
        public OptionSignalsRow Row { get; set; }
        public long CumulativeDelta { get; set; }
        public VPResult SessResult { get; set; }
        public VPResult RollResult { get; set; }

        // Bar volume breakdown for CSV
        public long BarVolume { get; set; }
        public long BarBuyVolume { get; set; }
        public long BarSellVolume { get; set; }
        public long BarDelta { get; set; }
    }

    /// <summary>
    /// Tracks VP state for a single option symbol (CE or PE at a strike).
    /// VP is computed at RangeATR bar close using ticks from that bar's time range.
    /// Owned by OptionSignalsComputeService, NOT by ViewModel.
    /// </summary>
    public class OptionVPState
    {
        public string Symbol { get; set; }
        public string Type { get; set; } // "CE" or "PE"
        public OptionSignalsRow Row { get; set; }

        // Session VP - accumulates all day, never expires
        public VPEngine SessionVPEngine { get; set; }

        // Rolling VP - 60-minute rolling window
        public RollingVolumeProfileEngine RollingVPEngine { get; set; }

        // CD Momentum Engine - smaMomentum applied to cumulative delta (Momentum + Smooth)
        public CDMomentumEngine CDMomoEngine { get; set; }

        // Price Momentum Engine - smaMomentum applied to price (Momentum + Smooth)
        public MomentumEngine PriceMomoEngine { get; set; }

        public BarsRequest RangeBarsRequest { get; set; }
        public BarsRequest TickBarsRequest { get; set; }
        public double LastClosePrice { get; set; }
        public int LastVPTickIndex { get; set; } = -1;
        public int LastRangeBarIndex { get; set; } = -1;
        public DateTime LastBarCloseTime { get; set; } = DateTime.MinValue;

        // Disposed flag to prevent accessing disposed subjects
        public bool IsDisposed { get; private set; }

        // Rx subjects for coordinating data readiness
        public BehaviorSubject<bool> TickDataReady { get; } = new BehaviorSubject<bool>(false);
        public BehaviorSubject<bool> RangeBarsReady { get; } = new BehaviorSubject<bool>(false);
        public Subject<BarsUpdateEventArgs> RangeBarUpdates { get; } = new Subject<BarsUpdateEventArgs>();
        public CompositeDisposable Subscriptions { get; } = new CompositeDisposable();

        // Trend tracking
        public HvnTrend LastSessionTrend { get; set; } = HvnTrend.Neutral;
        public HvnTrend LastRollingTrend { get; set; } = HvnTrend.Neutral;
        public DateTime? SessionTrendOnsetTime { get; set; }
        public DateTime? RollingTrendOnsetTime { get; set; }

        // Current bar volume tracking (for live mode CSV)
        public long CurrentBarVolume { get; set; }
        public long CurrentBarBuyVolume { get; set; }
        public long CurrentBarSellVolume { get; set; }

        /// <summary>
        /// Resets current bar volume accumulators. Call at start of new bar.
        /// </summary>
        public void ResetBarVolume()
        {
            CurrentBarVolume = 0;
            CurrentBarBuyVolume = 0;
            CurrentBarSellVolume = 0;
        }

        /// <summary>
        /// Adds tick volume to current bar accumulators.
        /// </summary>
        public void AddTickVolume(long volume, bool isBuy)
        {
            CurrentBarVolume += volume;
            if (isBuy) CurrentBarBuyVolume += volume;
            else CurrentBarSellVolume += volume;
        }

        /// <summary>
        /// Gets the current bar delta (buy - sell volume).
        /// </summary>
        public long CurrentBarDelta => CurrentBarBuyVolume - CurrentBarSellVolume;

        // Bar history for signal orchestrator - stores 256 bar snapshots
        public OptionBarHistory BarHistory { get; set; }

        // Simulation mode: pre-built bar replay data
        // Stores (barTime, closePrice, prevBarTime) for deterministic replay
        public List<(DateTime BarTime, double ClosePrice, DateTime PrevBarTime)> SimReplayBars { get; set; }
        public List<(DateTime Time, int Index)> SimTickTimes { get; set; }
        public int SimReplayBarIndex { get; set; } = 0;
        public int SimReplayTickIndex { get; set; } = 0;
        public double SimLastPrice { get; set; }
        public bool SimDataReady { get; set; } = false;
        public List<OptionSignalsRow> SimPrecomputedRows { get; set; } = new List<OptionSignalsRow>();

        // Pre-computed bar data with raw values for CSV writing during simulation playback
        public List<SimBarData> SimPrecomputedBars { get; set; } = new List<SimBarData>();
        public int SimCsvBarIndex { get; set; } = 0;

        // Flag to indicate pre-computation phase - prevents CSV writing during pre-computation
        public bool IsPrecomputing { get; set; } = false;

        public OptionVPState()
        {
            SessionVPEngine = new VPEngine();
            RollingVPEngine = new RollingVolumeProfileEngine(60); // 60-min rolling window

            // Initialize with dynamic tick intervals for options
            SessionVPEngine.Reset(0.50);
            RollingVPEngine.ResetWithDynamicInterval();

            // Initialize Momentum engines (smaMomentum style with Momentum + Smooth)
            CDMomoEngine = new CDMomentumEngine(14, 7);      // CD Momentum (period=14, smooth=7)
            PriceMomoEngine = new MomentumEngine(14, 7);    // Price Momentum (period=14, smooth=7)
        }

        public void Dispose()
        {
            IsDisposed = true;
            Subscriptions?.Dispose();
            TickDataReady?.Dispose();
            RangeBarsReady?.Dispose();
            RangeBarUpdates?.Dispose();
            RangeBarsRequest?.Dispose();
            TickBarsRequest?.Dispose();
        }
    }
}
