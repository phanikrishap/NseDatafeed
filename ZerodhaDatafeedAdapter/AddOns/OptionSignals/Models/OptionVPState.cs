using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
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
