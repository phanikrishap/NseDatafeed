using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.MarketData;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Service that computes Volume Profile metrics for NIFTY Futures using RangeATR bars.
    ///
    /// Architecture:
    /// ┌─────────────────────────────────────────────────────────────────────────────┐
    /// │                     HISTORICAL PHASE (Startup)                               │
    /// ├─────────────────────────────────────────────────────────────────────────────┤
    /// │ 1. Request 40 days of RangeATR bars (BarsPeriodType 7015)                   │
    /// │ 2. Request 40 days of Tick bars (BarsPeriodType.Tick)                       │
    /// │ 3. Build TickToBarIndex mapping for O(1) tick lookup per bar                │
    /// │ 4. For each RangeATR bar: extract ticks, build price ladder                 │
    /// │ 5. Compute POC, VAH, VAL, VWAP, HVNs from volume profile                   │
    /// └─────────────────────────────────────────────────────────────────────────────┘
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────────┐
    /// │                        LIVE PHASE (Real-time)                               │
    /// ├─────────────────────────────────────────────────────────────────────────────┤
    /// │ 1. Subscribe to WebSocket tick stream for NIFTY Futures                     │
    /// │ 2. Collect ticks in _pendingTicks buffer                                    │
    /// │ 3. On BarsRequest.Update (new RangeATR bar close):                          │
    /// │    a. Build price ladder from _pendingTicks                                 │
    /// │    b. Recalculate VP metrics                                                │
    /// │    c. Clear _pendingTicks buffer                                            │
    /// │ 4. Publish updated metrics via Rx stream                                    │
    /// └─────────────────────────────────────────────────────────────────────────────┘
    /// </summary>
    public class NiftyFuturesMetricsService : IDisposable
    {
        private static readonly Lazy<NiftyFuturesMetricsService> _instance =
            new Lazy<NiftyFuturesMetricsService>(() => new NiftyFuturesMetricsService());
        public static NiftyFuturesMetricsService Instance => _instance.Value;

        // NinjaTrader continuous contract symbol for NIFTY Futures
        private const string NIFTY_I_SYMBOL = "NIFTY_I";

        // ═══════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ═══════════════════════════════════════════════════════════════════

        private const int RANGE_ATR_BARS_TYPE = 7015;
        private const int RANGE_ATR_MINUTE_VALUE = 1;   // MinuteValue parameter
        private const int RANGE_ATR_MIN_SECONDS = 3;    // MinSeconds parameter
        private const int RANGE_ATR_MIN_TICKS = 1;      // MinTicks parameter
        private const int HISTORICAL_DAYS = 40;         // Days of history to load
        private const int SLICE_DAYS = 5;               // Days per parallel slice
        private const int NUM_SLICES = 8;               // 8 slices * 5 days = 40 days
        private const double VALUE_AREA_PERCENT = 0.70; // 70% value area
        private const double HVN_RATIO = 0.25;          // HVN threshold (25% of POC volume) - matches FutBias
        private const double VP_PRICE_INTERVAL = 1.0;   // 1 rupee price buckets for Volume Profile (like NinjaTrader)

        // ═══════════════════════════════════════════════════════════════════
        // STATE
        // ═══════════════════════════════════════════════════════════════════

        // NinjaTrader objects
        private BarsRequest _rangeATRBarsRequest;
        private BarsRequest _tickBarsRequest;
        private BarsRequest _minuteBarsRequest;      // 1-minute bars for time-indexed historical averages
        private Instrument _niftyFuturesInstrument;  // Zerodha symbol (NIFTY26JANFUT) for live WebSocket mapping
        private Instrument _niftyIInstrument;        // NT continuous symbol (NIFTY_I) for BarsRequest
        private string _niftyFuturesSymbol;          // Zerodha trading symbol (for logging/display)
        private double _tickSize = 0.05; // Default for NIFTY, will be updated from instrument

        // Internal Volume Profile Engine (simplified) - for Session VP
        private readonly InternalVolumeProfileEngine _vpEngine = new InternalVolumeProfileEngine();

        // Rolling Volume Profile Engine - 60-minute rolling window
        private const int ROLLING_WINDOW_MINUTES = 60;
        private readonly RollingVolumeProfileEngine _rollingVpEngine = new RollingVolumeProfileEngine(ROLLING_WINDOW_MINUTES);

        // Relative Metrics Engine for time-indexed historical comparisons (Session + Rolling)
        private readonly VPRelativeMetricsEngine _relMetrics = new VPRelativeMetricsEngine();
        private DateTime _lastMinuteBarTime = DateTime.MinValue;

        // Historical data storage
        private List<RangeATRBar> _historicalRangeATRBars = new List<RangeATRBar>();
        private List<HistoricalTick> _historicalTicks = new List<HistoricalTick>();

        // Tick-to-Bar mapping for O(1) lookup
        private InternalTickToBarMapper _tickMapper;

        // Live tick collection
        private readonly ConcurrentQueue<LiveTick> _pendingTicks = new ConcurrentQueue<LiveTick>();
        private DateTime _lastBarCloseTime = DateTime.MinValue;
        private int _lastBarIndex = -1;

        // Session tracking
        private DateTime _sessionStart = DateTime.MinValue;
        private DateTime _lastSessionDate = DateTime.MinValue; // For detecting new day
        private bool _isHistoricalPhaseComplete = false;
        private bool _isLivePhaseActive = false;

        // Reactive streams
        private readonly BehaviorSubject<NiftyFuturesVPMetrics> _metricsSubject =
            new BehaviorSubject<NiftyFuturesVPMetrics>(new NiftyFuturesVPMetrics { IsValid = false });
        public IObservable<NiftyFuturesVPMetrics> MetricsStream => _metricsSubject.AsObservable();

        // Latest metrics cache
        public NiftyFuturesVPMetrics LatestMetrics { get; private set; } = new NiftyFuturesVPMetrics { IsValid = false };

        // Subscription cleanup
        private readonly System.Reactive.Disposables.CompositeDisposable _disposables =
            new System.Reactive.Disposables.CompositeDisposable();
        private IDisposable _tickSubscription;
        private int _disposed = 0;

        // ═══════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ═══════════════════════════════════════════════════════════════════

        private NiftyFuturesMetricsService()
        {
            Logger.Info("[NiftyFuturesMetricsService] Constructor: Initializing singleton instance");
            RangeBarLogger.Info("[NiftyFuturesMetricsService] Initializing - RangeBar logging started");
        }

        /// <summary>
        /// Starts the service - resolves NIFTY Futures symbol and begins data loading.
        /// </summary>
        public async Task StartAsync()
        {
            Logger.Info("[NiftyFuturesMetricsService] StartAsync(): Starting service...");

            try
            {
                // Step 1: Wait for instrument database to be ready
                var dbReady = await MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                    .Timeout(TimeSpan.FromSeconds(90))
                    .FirstAsync()
                    .ToTask();

                if (!dbReady)
                {
                    Logger.Error("[NiftyFuturesMetricsService] StartAsync(): Instrument DB not ready");
                    return;
                }

                Logger.Info("[NiftyFuturesMetricsService] StartAsync(): Instrument DB ready");

                // Step 2: Resolve NIFTY Futures contract (reuse MarketAnalyzerLogic pattern)
                _niftyFuturesSymbol = await ResolveNiftyFuturesSymbol();
                if (string.IsNullOrEmpty(_niftyFuturesSymbol))
                {
                    Logger.Error("[NiftyFuturesMetricsService] StartAsync(): Failed to resolve NIFTY Futures symbol");
                    return;
                }

                Logger.Info($"[NiftyFuturesMetricsService] StartAsync(): Resolved NIFTY Futures symbol: {_niftyFuturesSymbol}");
                RangeBarLogger.Info($"[SERVICE_START] Symbol resolved: {_niftyFuturesSymbol}");

                // Step 3: Get NinjaTrader instrument handles (NIFTY_I for BarsRequest)
                await GetNTInstrument();
                if (_niftyIInstrument == null)
                {
                    Logger.Error($"[NiftyFuturesMetricsService] StartAsync(): Failed to get NT instrument for {NIFTY_I_SYMBOL}");
                    return;
                }

                _tickSize = _niftyIInstrument.MasterInstrument.TickSize;
                Logger.Info($"[NiftyFuturesMetricsService] StartAsync(): Got {NIFTY_I_SYMBOL} instrument, tickSize={_tickSize}, VP priceInterval={VP_PRICE_INTERVAL}");
                RangeBarLogger.Info($"[INSTRUMENT] {NIFTY_I_SYMBOL} tickSize={_tickSize}, VP priceInterval={VP_PRICE_INTERVAL}");

                // Step 4: Request historical data (RangeATR + Tick bars)
                await RequestHistoricalData();

                // Step 5: Subscribe to live tick stream
                SubscribeToLiveTicks();

                Logger.Info("[NiftyFuturesMetricsService] StartAsync(): Service started successfully");
            }
            catch (TimeoutException)
            {
                Logger.Error("[NiftyFuturesMetricsService] StartAsync(): Timeout waiting for InstrumentDbReady");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] StartAsync(): Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stops the service and cleans up resources.
        /// </summary>
        public void Stop()
        {
            Logger.Info("[NiftyFuturesMetricsService] Stop(): Stopping service...");

            _isLivePhaseActive = false;
            _tickSubscription?.Dispose();

            // Cleanup BarsRequests on UI thread
            _ = NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                if (_rangeATRBarsRequest != null)
                {
                    _rangeATRBarsRequest.Update -= OnRangeATRBarsUpdate;
                    _rangeATRBarsRequest.Dispose();
                    _rangeATRBarsRequest = null;
                }

                if (_tickBarsRequest != null)
                {
                    _tickBarsRequest.Update -= OnTickBarsUpdate;
                    _tickBarsRequest.Dispose();
                    _tickBarsRequest = null;
                }
            });

            Logger.Info("[NiftyFuturesMetricsService] Stop(): Service stopped");
        }

        // ═══════════════════════════════════════════════════════════════════
        // SYMBOL RESOLUTION
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the current NIFTY Futures contract symbol from SQLite database.
        /// Pattern: NIFTY{YY}{MMM}FUT (e.g., NIFTY26JANFUT)
        /// </summary>
        private async Task<string> ResolveNiftyFuturesSymbol()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Query SQLite for NFO-FUT segment, NIFTY underlying, nearest expiry
                    var (token, tradingSymbol) = Instruments.InstrumentManager.Instance.LookupFuturesInSqlite(
                        "NFO-FUT",
                        "NIFTY",
                        DateTime.Today);

                    if (token > 0 && !string.IsNullOrEmpty(tradingSymbol))
                    {
                        Logger.Info($"[NiftyFuturesMetricsService] ResolveNiftyFuturesSymbol(): Found {tradingSymbol} (token={token})");
                        return tradingSymbol;
                    }

                    Logger.Warn("[NiftyFuturesMetricsService] ResolveNiftyFuturesSymbol(): No futures contract found");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[NiftyFuturesMetricsService] ResolveNiftyFuturesSymbol(): Exception - {ex.Message}", ex);
                    return null;
                }
            });
        }

        /// <summary>
        /// Gets the NinjaTrader Instrument handles for both NIFTY_I (for BarsRequest)
        /// and the Zerodha symbol (for WebSocket mapping).
        /// </summary>
        private async Task GetNTInstrument()
        {
            await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                // Get NIFTY_I for BarsRequest (this is the NT continuous contract)
                _niftyIInstrument = Instrument.GetInstrument(NIFTY_I_SYMBOL);
                if (_niftyIInstrument == null)
                {
                    Logger.Warn($"[NiftyFuturesMetricsService] GetNTInstrument(): Instrument not found for {NIFTY_I_SYMBOL}");
                }
                else
                {
                    Logger.Info($"[NiftyFuturesMetricsService] GetNTInstrument(): Got NIFTY_I instrument");
                    RangeBarLogger.Info($"[INSTRUMENT] Using {NIFTY_I_SYMBOL} for BarsRequest");
                }

                // Also get the Zerodha symbol (for reference/WebSocket mapping)
                _niftyFuturesInstrument = Instrument.GetInstrument(_niftyFuturesSymbol);
                if (_niftyFuturesInstrument == null)
                {
                    Logger.Warn($"[NiftyFuturesMetricsService] GetNTInstrument(): Instrument not found for {_niftyFuturesSymbol}");
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        // HISTORICAL DATA LOADING (Parallel Slices)
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Requests RangeATR and Tick historical data using parallel 5-day slices.
        /// 8 slices * 5 days = 40 days total, loaded in parallel for speed.
        /// </summary>
        private async Task RequestHistoricalData()
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            Logger.Info($"[NiftyFuturesMetricsService] RequestHistoricalData(): Starting parallel load - {NUM_SLICES} slices x {SLICE_DAYS} days = {HISTORICAL_DAYS} days");
            RangeBarLogger.Info($"[PARALLEL_LOAD] Starting: {NUM_SLICES} slices x {SLICE_DAYS} days, symbol={NIFTY_I_SYMBOL}");

            // Create slice date ranges (most recent first for faster initial display)
            var sliceRanges = new List<(DateTime from, DateTime to, int sliceNum)>();
            var now = DateTime.Now;

            for (int i = 0; i < NUM_SLICES; i++)
            {
                // Slice 0 = today to 5 days ago, Slice 1 = 5-10 days ago, etc.
                var toDate = now.Date.AddDays(1).AddDays(-i * SLICE_DAYS);  // End of day
                var fromDate = toDate.AddDays(-SLICE_DAYS);
                sliceRanges.Add((fromDate, toDate, i));
            }

            // Concurrent collections for thread-safe aggregation
            var allBars = new ConcurrentBag<RangeATRBar>();
            var allTicks = new ConcurrentBag<HistoricalTick>();
            var sliceTasks = new List<Task<(int sliceNum, int bars, int ticks, long elapsedMs, bool success)>>();

            // Launch all slice requests in parallel
            foreach (var slice in sliceRanges)
            {
                var task = RequestSliceAsync(slice.from, slice.to, slice.sliceNum, allBars, allTicks);
                sliceTasks.Add(task);
            }

            // Wait for all slices to complete
            var results = await Task.WhenAll(sliceTasks);

            totalStopwatch.Stop();

            // Log results
            int totalBars = 0, totalTicks = 0, successCount = 0;
            foreach (var result in results.OrderBy(r => r.sliceNum))
            {
                RangeBarLogger.Info($"[SLICE_{result.sliceNum}] Bars={result.bars}, Ticks={result.ticks}, Time={result.elapsedMs}ms, Success={result.success}");
                if (result.success)
                {
                    totalBars += result.bars;
                    totalTicks += result.ticks;
                    successCount++;
                }
            }

            RangeBarLogger.Info($"[PARALLEL_COMPLETE] TotalBars={totalBars}, TotalTicks={totalTicks}, Slices={successCount}/{NUM_SLICES}, TotalTime={totalStopwatch.ElapsedMilliseconds}ms");
            Logger.Info($"[NiftyFuturesMetricsService] Parallel load complete: {totalBars} bars, {totalTicks} ticks in {totalStopwatch.ElapsedMilliseconds}ms");

            // Convert to sorted lists
            _historicalRangeATRBars = allBars.OrderBy(b => b.Time).ToList();
            _historicalTicks = allTicks.OrderBy(t => t.Time).ToList();

            // Re-index bars after sorting
            for (int i = 0; i < _historicalRangeATRBars.Count; i++)
                _historicalRangeATRBars[i].Index = i;

            // Apply uptick/downtick rule for IsBuy classification (like NinjaTrader OrderFlowEngine)
            ApplyUptickDowntickRule();

            if (successCount > 0)
            {
                RangeBarLogger.LogHistoricalProgress(_niftyFuturesSymbol, _historicalRangeATRBars.Count, _historicalTicks.Count,
                    DateTime.Now.AddDays(-HISTORICAL_DAYS), DateTime.Now);

                // Build tick-to-bar mapping
                var mapStopwatch = System.Diagnostics.Stopwatch.StartNew();
                BuildTickToBarMapping();
                mapStopwatch.Stop();
                RangeBarLogger.Info($"[TICK_MAP] Built in {mapStopwatch.ElapsedMilliseconds}ms");

                // Process historical data through VolumeProfileEngine
                var vpStopwatch = System.Diagnostics.Stopwatch.StartNew();
                ProcessHistoricalVolumeProfile();
                vpStopwatch.Stop();
                RangeBarLogger.Info($"[VP_CALC] Processed in {vpStopwatch.ElapsedMilliseconds}ms");

                _isHistoricalPhaseComplete = true;
                _isLivePhaseActive = true;

                Logger.Info("[NiftyFuturesMetricsService] RequestHistoricalData(): Historical phase complete, live phase active");
                RangeBarLogger.Info($"[PHASE_CHANGE] Historical phase complete, live phase active");

                // Set up the live update handler on the most recent slice's BarsRequest
                await SetupLiveUpdateHandler();
            }
            else
            {
                Logger.Error("[NiftyFuturesMetricsService] RequestHistoricalData(): All slices failed");
                RangeBarLogger.Error("[PARALLEL_LOAD] All slices failed!");
            }
        }

        /// <summary>
        /// Requests a single 5-day slice of RangeATR bars and ticks.
        /// </summary>
        private async Task<(int sliceNum, int bars, int ticks, long elapsedMs, bool success)> RequestSliceAsync(
            DateTime fromDate, DateTime toDate, int sliceNum,
            ConcurrentBag<RangeATRBar> allBars, ConcurrentBag<HistoricalTick> allTicks)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int barsLoaded = 0, ticksLoaded = 0;
            bool success = false;

            try
            {
                var barsComplete = new TaskCompletionSource<bool>();
                var ticksComplete = new TaskCompletionSource<bool>();

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    // Request RangeATR bars for this slice
                    var barsRequest = new BarsRequest(_niftyIInstrument, fromDate, toDate);
                    barsRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = (BarsPeriodType)RANGE_ATR_BARS_TYPE,
                        Value = RANGE_ATR_MINUTE_VALUE,
                        Value2 = RANGE_ATR_MIN_SECONDS,
                        BaseBarsPeriodValue = RANGE_ATR_MIN_TICKS
                    };
                    barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    barsRequest.Request((request, errorCode, errorMessage) =>
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            for (int i = 0; i < request.Bars.Count; i++)
                            {
                                allBars.Add(new RangeATRBar
                                {
                                    Index = i,
                                    Time = request.Bars.GetTime(i),
                                    Open = request.Bars.GetOpen(i),
                                    High = request.Bars.GetHigh(i),
                                    Low = request.Bars.GetLow(i),
                                    Close = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i)
                                });
                            }
                            barsLoaded = request.Bars.Count;
                            barsComplete.TrySetResult(true);
                        }
                        else
                        {
                            Logger.Warn($"[NiftyFuturesMetricsService] Slice {sliceNum} bars failed: {errorCode} - {errorMessage}");
                            barsComplete.TrySetResult(false);
                        }
                        request.Dispose();
                    });

                    // Request Tick bars for this slice
                    var tickRequest = new BarsRequest(_niftyIInstrument, fromDate, toDate);
                    tickRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = BarsPeriodType.Tick,
                        Value = 1
                    };
                    tickRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    tickRequest.Request((request, errorCode, errorMessage) =>
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            for (int i = 0; i < request.Bars.Count; i++)
                            {
                                allTicks.Add(new HistoricalTick
                                {
                                    Index = i,
                                    Time = request.Bars.GetTime(i),
                                    Price = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i),
                                    IsBuy = request.Bars.GetClose(i) >= request.Bars.GetOpen(i)
                                });
                            }
                            ticksLoaded = request.Bars.Count;
                            ticksComplete.TrySetResult(true);
                        }
                        else
                        {
                            Logger.Warn($"[NiftyFuturesMetricsService] Slice {sliceNum} ticks failed: {errorCode} - {errorMessage}");
                            ticksComplete.TrySetResult(false);
                        }
                        request.Dispose();
                    });
                });

                // Wait for both requests in this slice
                var barsResult = await barsComplete.Task;
                var ticksResult = await ticksComplete.Task;
                success = barsResult && ticksResult;
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] Slice {sliceNum} exception: {ex.Message}", ex);
            }

            stopwatch.Stop();
            return (sliceNum, barsLoaded, ticksLoaded, stopwatch.ElapsedMilliseconds, success);
        }

        /// <summary>
        /// Sets up the live update handler on a new BarsRequest for real-time updates.
        /// </summary>
        private async Task SetupLiveUpdateHandler()
        {
            await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                // Create a new request for live updates (last few bars + live)
                _rangeATRBarsRequest = new BarsRequest(_niftyIInstrument, 100);
                _rangeATRBarsRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = (BarsPeriodType)RANGE_ATR_BARS_TYPE,
                    Value = RANGE_ATR_MINUTE_VALUE,
                    Value2 = RANGE_ATR_MIN_SECONDS,
                    BaseBarsPeriodValue = RANGE_ATR_MIN_TICKS
                };
                _rangeATRBarsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                _rangeATRBarsRequest.Update += OnRangeATRBarsUpdate;

                _rangeATRBarsRequest.Request((request, errorCode, errorMessage) =>
                {
                    if (errorCode == ErrorCode.NoError)
                    {
                        _lastBarIndex = request.Bars.Count - 1;
                        _lastBarCloseTime = request.Bars.GetTime(_lastBarIndex);
                        RangeBarLogger.Info($"[LIVE_HANDLER] Attached, lastBarIndex={_lastBarIndex}");
                    }
                    else
                    {
                        Logger.Error($"[NiftyFuturesMetricsService] Live handler setup failed: {errorCode} - {errorMessage}");
                    }
                });

                // Also set up tick request for live ticks
                _tickBarsRequest = new BarsRequest(_niftyIInstrument, 1000);
                _tickBarsRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = BarsPeriodType.Tick,
                    Value = 1
                };
                _tickBarsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                _tickBarsRequest.Update += OnTickBarsUpdate;

                _tickBarsRequest.Request((request, errorCode, errorMessage) =>
                {
                    if (errorCode == ErrorCode.NoError)
                    {
                        RangeBarLogger.Info($"[LIVE_TICKS] Attached");
                    }
                });

                // Set up 1-minute BarsRequest for time-indexed historical averages
                _minuteBarsRequest = new BarsRequest(_niftyIInstrument, HISTORICAL_DAYS * 400); // ~400 minutes per trading day
                _minuteBarsRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = BarsPeriodType.Minute,
                    Value = 1
                };
                _minuteBarsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                _minuteBarsRequest.Update += OnMinuteBarsUpdate;

                _minuteBarsRequest.Request((request, errorCode, errorMessage) =>
                {
                    if (errorCode == ErrorCode.NoError)
                    {
                        RangeBarLogger.Info($"[LIVE_MINUTE] Attached, bars={request.Bars.Count}");
                    }
                    else
                    {
                        Logger.Warn($"[NiftyFuturesMetricsService] Minute bars request failed: {errorCode} - {errorMessage}");
                    }
                });
            });
        }

        /// <summary>
        /// Handles 1-minute bar updates for time-indexed relative metrics.
        /// On each minute bar close, samples current VP state and updates historical averages.
        /// </summary>
        private void OnMinuteBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            if (!_isLivePhaseActive) return;

            try
            {
                var bars = _minuteBarsRequest.Bars;
                if (bars == null || bars.Count == 0) return;

                DateTime minuteBarTime = bars.GetTime(e.MaxIndex);

                // Only process if this is a new minute (not an update to existing bar)
                if (minuteBarTime <= _lastMinuteBarTime) return;
                _lastMinuteBarTime = minuteBarTime;

                // Sample current VP state at this minute boundary
                // Set close price for HVN classification (HVNs at/below close = Buy, above = Sell)
                _vpEngine.SetClosePrice(bars.GetClose(e.MaxIndex));
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                if (!vpMetrics.IsValid) return;

                // Update historical averages (for building reference data)
                _relMetrics.UpdateHistory(minuteBarTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                // Calculate relative/cumulative metrics for this minute
                _relMetrics.Update(minuteBarTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                // Log at DEBUG level
                RangeBarLogger.Debug($"[MINUTE_SAMPLE] {minuteBarTime:HH:mm} | HVNBuy={vpMetrics.HVNBuyCount} HVNSell={vpMetrics.HVNSellCount} " +
                    $"| RelB={_relMetrics.RelHVNBuy[0]:F0} RelS={_relMetrics.RelHVNSell[0]:F0} " +
                    $"| CumB={_relMetrics.CumHVNBuyRank[0]:F0} CumS={_relMetrics.CumHVNSellRank[0]:F0}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] OnMinuteBarsUpdate(): Exception - {ex.Message}", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // UPTICK/DOWNTICK CLASSIFICATION
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Applies the uptick/downtick rule to classify tick direction.
        /// This matches NinjaTrader's OrderFlowEngine.ProcessTickAuto() method:
        /// - UPTICK (price > prevPrice): Volume = Buy
        /// - DOWNTICK (price < prevPrice): Volume = Sell
        /// - NEUTRAL (price == prevPrice): Keep previous direction (or split 50/50 for volume)
        /// </summary>
        private void ApplyUptickDowntickRule()
        {
            if (_historicalTicks == null || _historicalTicks.Count == 0)
                return;

            int buyCount = 0;
            int sellCount = 0;
            double prevPrice = 0;
            bool lastDirection = true; // Default to buy for first tick

            for (int i = 0; i < _historicalTicks.Count; i++)
            {
                var tick = _historicalTicks[i];
                double price = tick.Price;

                if (i == 0)
                {
                    // First tick - use original classification or default to buy
                    tick.IsBuy = tick.IsBuy;
                    lastDirection = tick.IsBuy;
                }
                else if (price > prevPrice)
                {
                    // UPTICK = BUY
                    tick.IsBuy = true;
                    lastDirection = true;
                }
                else if (price < prevPrice)
                {
                    // DOWNTICK = SELL
                    tick.IsBuy = false;
                    lastDirection = false;
                }
                else
                {
                    // NEUTRAL - use last direction
                    tick.IsBuy = lastDirection;
                }

                prevPrice = price;

                if (tick.IsBuy)
                    buyCount++;
                else
                    sellCount++;
            }

            Logger.Info($"[NiftyFuturesMetricsService] ApplyUptickDowntickRule(): {_historicalTicks.Count} ticks processed - Buy={buyCount}, Sell={sellCount}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // TICK-TO-BAR MAPPING
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds an efficient mapping from RangeATR bars to tick indices.
        /// </summary>
        private void BuildTickToBarMapping()
        {
            Logger.Info("[NiftyFuturesMetricsService] BuildTickToBarMapping(): Building tick-to-bar index...");

            _tickMapper = new InternalTickToBarMapper(_historicalRangeATRBars, _historicalTicks);
            _tickMapper.BuildIndex();

            Logger.Info($"[NiftyFuturesMetricsService] BuildTickToBarMapping(): Index built. Mapped {_tickMapper.MappedBarsCount} bars");
        }

        // ═══════════════════════════════════════════════════════════════════
        // VOLUME PROFILE CALCULATION
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Processes all historical RangeATR bars through VolumeProfileEngine.
        /// Processes each session separately, logging bar-by-bar VP evolution at DEBUG level.
        /// </summary>
        private void ProcessHistoricalVolumeProfile()
        {
            Logger.Info("[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Processing historical data...");

            if (_historicalRangeATRBars.Count == 0)
            {
                Logger.Warn("[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): No bars to process");
                return;
            }

            // Group bars by session date
            var sessionGroups = _historicalRangeATRBars
                .GroupBy(b => b.Time.Date)
                .OrderBy(g => g.Key)
                .ToList();

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Found {sessionGroups.Count} sessions");
            RangeBarLogger.Info($"[HISTORICAL_SESSIONS] Processing {sessionGroups.Count} sessions, {_historicalRangeATRBars.Count} total bars");

            int totalProcessedBars = 0;
            int totalBarsWithTicks = 0;
            int totalMinuteSamples = 0;

            // Process each session
            foreach (var sessionGroup in sessionGroups)
            {
                DateTime sessionDate = sessionGroup.Key;
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();

                // Reset VP engines for new session
                _vpEngine.Reset(VP_PRICE_INTERVAL);
                _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
                _sessionStart = sessionBars.First().Time;

                // Track minute boundaries for RelMetrics historical data
                DateTime lastMinuteSampled = DateTime.MinValue;

                // Log session start at DEBUG level
                RangeBarLogger.Debug($"[SESSION] {sessionDate:yyyy-MM-dd} | Bars={sessionBars.Count} | Start={_sessionStart:HH:mm:ss}");

                int sessionBarIndex = 0;
                foreach (var bar in sessionBars)
                {
                    // Get ticks for this bar
                    var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);

                    if (ticksForBar.Count > 0)
                    {
                        // Add ticks to Session VP engine
                        foreach (var tick in ticksForBar)
                        {
                            _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                        }

                        // Add ticks to Rolling VP engine with timestamp for expiration
                        foreach (var tick in ticksForBar)
                        {
                            _rollingVpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);
                        }

                        totalBarsWithTicks++;
                    }

                    // Expire old data from rolling VP (60-minute window)
                    _rollingVpEngine.ExpireOldData(bar.Time);

                    sessionBarIndex++;
                    totalProcessedBars++;

                    // Calculate current VP metrics after this bar
                    // Set close price for HVN classification (HVNs at/below close = Buy, above = Sell)
                    _vpEngine.SetClosePrice(bar.Close);
                    _rollingVpEngine.SetClosePrice(bar.Close);
                    var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                    var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                    // Sample at minute boundaries for RelMetrics historical averages
                    // This builds the _avgByTime dictionary that relative metrics use as reference
                    if (vpMetrics.IsValid)
                    {
                        DateTime minuteBoundary = new DateTime(bar.Time.Year, bar.Time.Month, bar.Time.Day,
                            bar.Time.Hour, bar.Time.Minute, 0);

                        if (minuteBoundary > lastMinuteSampled)
                        {
                            // New minute - sample Session VP state for historical reference
                            _relMetrics.UpdateHistory(minuteBoundary, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                            // Sample Rolling VP state for historical reference
                            if (rollingVpMetrics.IsValid)
                            {
                                _relMetrics.UpdateRollingHistory(minuteBoundary,
                                    rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                            }

                            lastMinuteSampled = minuteBoundary;
                            totalMinuteSamples++;
                        }

                        // Log bar-by-bar VP evolution at DEBUG level
                        string hvnStr = vpMetrics.HVNs != null && vpMetrics.HVNs.Count > 0
                            ? string.Join("|", vpMetrics.HVNs.ConvertAll(h => h.ToString("F2")))
                            : "-";

                        RangeBarLogger.Debug($"{bar.Time:HH:mm:ss},{bar.Close:F2},{vpMetrics.VAH:F2},{vpMetrics.VAL:F2},{vpMetrics.POC:F2},{hvnStr}");
                    }
                }

                // Log session summary at DEBUG level
                var finalMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                if (finalMetrics.IsValid)
                {
                    RangeBarLogger.Debug($"[SESSION_END] {sessionDate:yyyy-MM-dd} | POC={finalMetrics.POC:F2} VAH={finalMetrics.VAH:F2} VAL={finalMetrics.VAL:F2} VWAP={finalMetrics.VWAP:F2} | Bars={sessionBars.Count}");
                }
            }

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Built RelMetrics reference from {totalMinuteSamples} minute samples");

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Processed {totalProcessedBars} bars across {sessionGroups.Count} sessions, {totalBarsWithTicks} with tick data");

            // Set _lastSessionDate for live session detection
            _lastSessionDate = sessionGroups.Last().Key;

            // For final metrics, we want TODAY's session (or last session if today has no data)
            // Re-process through RelMetrics to accumulate cumulative values properly
            var todaysBars = _historicalRangeATRBars.Where(b => b.Time.Date == DateTime.Today).ToList();
            List<RangeATRBar> targetSessionBars;
            DateTime targetSessionDate;

            if (todaysBars.Count > 0)
            {
                targetSessionBars = todaysBars;
                targetSessionDate = DateTime.Today;
                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from today's {todaysBars.Count} bars");
            }
            else
            {
                // Use the last available session
                var lastSession = sessionGroups.Last();
                targetSessionBars = lastSession.ToList();
                targetSessionDate = lastSession.Key;
                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from last session {lastSession.Key:yyyy-MM-dd} ({lastSession.Count()} bars)");
            }

            // Re-process target session through VP engines AND RelMetrics to build cumulative values
            _vpEngine.Reset(VP_PRICE_INTERVAL);
            _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
            _sessionStart = targetSessionBars.First().Time;

            // Start a fresh session in RelMetrics for cumulative tracking
            _relMetrics.StartSession(_sessionStart);

            DateTime lastMinuteSampledForCumul = DateTime.MinValue;
            int barsProcessedForCumul = 0;

            foreach (var bar in targetSessionBars)
            {
                // Add ticks to Session VP engine
                var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);
                foreach (var tick in ticksForBar)
                {
                    _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                }

                // Add ticks to Rolling VP engine with timestamp
                foreach (var tick in ticksForBar)
                {
                    _rollingVpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);
                }

                // Expire old data from rolling VP
                _rollingVpEngine.ExpireOldData(bar.Time);

                // Calculate VP metrics after this bar
                _vpEngine.SetClosePrice(bar.Close);
                _rollingVpEngine.SetClosePrice(bar.Close);
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                // Sample at minute boundaries for RelMetrics cumulative tracking
                // This builds up _sessionCumul and _sessionRef properly
                if (vpMetrics.IsValid)
                {
                    DateTime minuteBoundary = new DateTime(bar.Time.Year, bar.Time.Month, bar.Time.Day,
                        bar.Time.Hour, bar.Time.Minute, 0);

                    if (minuteBoundary > lastMinuteSampledForCumul)
                    {
                        // Update Session RelMetrics - this accumulates session totals
                        _relMetrics.Update(minuteBoundary, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                        // Update Rolling RelMetrics - this accumulates rolling totals
                        if (rollingVpMetrics.IsValid)
                        {
                            _relMetrics.UpdateRolling(minuteBoundary,
                                rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                        }

                        lastMinuteSampledForCumul = minuteBoundary;
                        barsProcessedForCumul++;
                    }
                }
            }

            // Log cumulative tracking details
            var sessionTotals = _relMetrics.GetSessionTotals();
            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Cumulative tracking - " +
                $"Bars={barsProcessedForCumul}, CumHVNBuy={sessionTotals.cumHVNBuy:F1}/Ref={sessionTotals.refHVNBuy:F1}, " +
                $"CumHVNSell={sessionTotals.cumHVNSell:F1}/Ref={sessionTotals.refHVNSell:F1}, " +
                $"CumValW={sessionTotals.cumValWidth:F1}/Ref={sessionTotals.refValWidth:F1}");

            // Calculate and publish final metrics - skip RelMetrics.Update since we already processed the session
            RecalculateAndPublishMetrics(skipRelMetricsUpdate: true);
        }

        /// <summary>
        /// Recalculates VP metrics and publishes to stream.
        /// </summary>
        /// <param name="skipRelMetricsUpdate">If true, skip the RelMetrics.Update call (use when session was already processed)</param>
        private void RecalculateAndPublishMetrics(bool skipRelMetricsUpdate = false)
        {
            try
            {
                // Calculate Session VP metrics
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                // Calculate Rolling VP metrics
                var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                // Update RelMetrics with current VP state to compute relative/cumulative values
                // This uses the _avgByTime built from historical data as reference
                // Skip if we've already processed the session (e.g., during historical load)
                if (vpMetrics.IsValid && !skipRelMetricsUpdate)
                {
                    DateTime barTime = _lastBarCloseTime != DateTime.MinValue ? _lastBarCloseTime : DateTime.Now;
                    _relMetrics.Update(barTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                    // Also update rolling metrics
                    if (rollingVpMetrics.IsValid)
                    {
                        _relMetrics.UpdateRolling(barTime,
                            rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                    }
                }

                // Get Session relative/cumulative metrics (from circular buffers, [0] = latest)
                double relHVNBuy = _relMetrics.RelHVNBuy.Count > 0 ? _relMetrics.RelHVNBuy[0] : 0;
                double relHVNSell = _relMetrics.RelHVNSell.Count > 0 ? _relMetrics.RelHVNSell[0] : 0;
                double relValueWidth = _relMetrics.RelValueWidth.Count > 0 ? _relMetrics.RelValueWidth[0] : 0;
                double cumHVNBuyRank = _relMetrics.CumHVNBuyRank.Count > 0 ? _relMetrics.CumHVNBuyRank[0] : 100;
                double cumHVNSellRank = _relMetrics.CumHVNSellRank.Count > 0 ? _relMetrics.CumHVNSellRank[0] : 100;
                double cumValueWidthRank = _relMetrics.CumValueWidthRank.Count > 0 ? _relMetrics.CumValueWidthRank[0] : 100;

                // Get Rolling relative/cumulative metrics
                double relHVNBuyRolling = _relMetrics.RelHVNBuyRolling.Count > 0 ? _relMetrics.RelHVNBuyRolling[0] : 0;
                double relHVNSellRolling = _relMetrics.RelHVNSellRolling.Count > 0 ? _relMetrics.RelHVNSellRolling[0] : 0;
                double relValueWidthRolling = _relMetrics.RelValueWidthRolling.Count > 0 ? _relMetrics.RelValueWidthRolling[0] : 0;
                double cumHVNBuyRollingRank = _relMetrics.CumHVNBuyRollingRank.Count > 0 ? _relMetrics.CumHVNBuyRollingRank[0] : 100;
                double cumHVNSellRollingRank = _relMetrics.CumHVNSellRollingRank.Count > 0 ? _relMetrics.CumHVNSellRollingRank[0] : 100;
                double cumValueWidthRollingRank = _relMetrics.CumValueWidthRollingRank.Count > 0 ? _relMetrics.CumValueWidthRollingRank[0] : 100;

                var metrics = new NiftyFuturesVPMetrics
                {
                    // ═══════════════════════════════════════════════════════════════════
                    // SESSION VP METRICS
                    // ═══════════════════════════════════════════════════════════════════

                    // Core Session VP metrics
                    POC = vpMetrics.POC,
                    VAH = vpMetrics.VAH,
                    VAL = vpMetrics.VAL,
                    VWAP = vpMetrics.VWAP,
                    HVNs = vpMetrics.HVNs,

                    // Session HVN Buy/Sell breakdown
                    HVNBuyCount = vpMetrics.HVNBuyCount,
                    HVNSellCount = vpMetrics.HVNSellCount,
                    HVNBuyVolume = vpMetrics.HVNBuyVolume,
                    HVNSellVolume = vpMetrics.HVNSellVolume,

                    // Session Relative metrics
                    RelHVNBuy = relHVNBuy,
                    RelHVNSell = relHVNSell,
                    RelValueWidth = relValueWidth,

                    // Session Cumulative metrics
                    CumHVNBuyRank = cumHVNBuyRank,
                    CumHVNSellRank = cumHVNSellRank,
                    CumValueWidthRank = cumValueWidthRank,

                    // ═══════════════════════════════════════════════════════════════════
                    // ROLLING VP METRICS (60-minute window)
                    // ═══════════════════════════════════════════════════════════════════

                    // Core Rolling VP metrics
                    RollingPOC = rollingVpMetrics.IsValid ? rollingVpMetrics.POC : 0,
                    RollingVAH = rollingVpMetrics.IsValid ? rollingVpMetrics.VAH : 0,
                    RollingVAL = rollingVpMetrics.IsValid ? rollingVpMetrics.VAL : 0,

                    // Rolling HVN Buy/Sell breakdown
                    RollingHVNBuyCount = rollingVpMetrics.IsValid ? rollingVpMetrics.HVNBuyCount : 0,
                    RollingHVNSellCount = rollingVpMetrics.IsValid ? rollingVpMetrics.HVNSellCount : 0,

                    // Rolling Relative metrics
                    RelHVNBuyRolling = relHVNBuyRolling,
                    RelHVNSellRolling = relHVNSellRolling,
                    RelValueWidthRolling = relValueWidthRolling,

                    // Rolling Cumulative metrics
                    CumHVNBuyRollingRank = cumHVNBuyRollingRank,
                    CumHVNSellRollingRank = cumHVNSellRollingRank,
                    CumValueWidthRollingRank = cumValueWidthRollingRank,

                    // ═══════════════════════════════════════════════════════════════════
                    // METADATA
                    // ═══════════════════════════════════════════════════════════════════

                    BarCount = _historicalRangeATRBars.Count(b => b.Time >= _sessionStart),
                    LastBarTime = _lastBarCloseTime,
                    LastUpdate = DateTime.Now,
                    Symbol = _niftyFuturesSymbol,
                    IsValid = vpMetrics.IsValid
                };

                LatestMetrics = metrics;
                _metricsSubject.OnNext(metrics);

                // Log Session + Rolling metrics
                double valueWidth = metrics.VAH - metrics.VAL;
                double rollingValueWidth = metrics.RollingVAH - metrics.RollingVAL;
                Logger.Info($"[NiftyFuturesMetricsService] Session: POC={metrics.POC:F2}, ValW={valueWidth:F0}, HVNBuy={metrics.HVNBuyCount}, HVNSell={metrics.HVNSellCount} | " +
                    $"Rel: B={relHVNBuy:F0} S={relHVNSell:F0} W={relValueWidth:F0} | Cum: B={cumHVNBuyRank:F0} S={cumHVNSellRank:F0} W={cumValueWidthRank:F0}");
                Logger.Info($"[NiftyFuturesMetricsService] Rolling: POC={metrics.RollingPOC:F2}, ValW={rollingValueWidth:F0}, HVNBuy={metrics.RollingHVNBuyCount}, HVNSell={metrics.RollingHVNSellCount} | " +
                    $"Rel: B={relHVNBuyRolling:F0} S={relHVNSellRolling:F0} W={relValueWidthRolling:F0} | Cum: B={cumHVNBuyRollingRank:F0} S={cumHVNSellRollingRank:F0} W={cumValueWidthRollingRank:F0}");
                RangeBarLogger.LogVPMetrics(_niftyFuturesSymbol, metrics.POC, metrics.VAH, metrics.VAL, metrics.VWAP, metrics.HVNs?.Count ?? 0, metrics.BarCount);
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] RecalculateAndPublishMetrics(): Exception - {ex.Message}", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // LIVE DATA HANDLING
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Subscribes to live tick stream from WebSocket.
        /// </summary>
        private void SubscribeToLiveTicks()
        {
            Logger.Info("[NiftyFuturesMetricsService] SubscribeToLiveTicks(): Subscribing to live tick stream...");

            // Subscribe to tick events via NiftyFuturesStream
            _tickSubscription = MarketDataReactiveHub.Instance.NiftyFuturesStream
                .Where(update => update != null && update.Symbol != null)
                .Subscribe(OnLiveTickReceived);

            Logger.Info("[NiftyFuturesMetricsService] SubscribeToLiveTicks(): Subscribed to live ticks");
        }

        /// <summary>
        /// Handles incoming live tick data.
        /// </summary>
        private void OnLiveTickReceived(IndexPriceUpdate update)
        {
            if (!_isLivePhaseActive) return;

            // Queue the tick for processing
            _pendingTicks.Enqueue(new LiveTick
            {
                Time = DateTime.Now,
                Price = update.Price,
                Volume = 1, // WebSocket doesn't provide volume per tick
                IsBuy = true // Will be refined based on price direction
            });
        }

        /// <summary>
        /// Handles RangeATR bar updates (new bar close).
        /// </summary>
        private void OnRangeATRBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            if (!_isLivePhaseActive) return;

            try
            {
                // Check if this is a new bar (not just an update to existing bar)
                if (e.MaxIndex > _lastBarIndex)
                {
                    _lastBarIndex = e.MaxIndex;
                    _lastBarCloseTime = _rangeATRBarsRequest.Bars.GetTime(e.MaxIndex);
                    var bars = _rangeATRBarsRequest.Bars;

                    // Check for new trading day and reset VP if needed
                    DateTime currentBarDate = _lastBarCloseTime.Date;
                    if (_lastSessionDate != DateTime.MinValue && currentBarDate > _lastSessionDate)
                    {
                        // New trading day detected - reset Volume Profile
                        Logger.Info($"[NiftyFuturesMetricsService] New session detected: {currentBarDate:yyyy-MM-dd}");
                        RangeBarLogger.LogSessionReset(_niftyFuturesSymbol, currentBarDate);

                        _vpEngine.Reset(VP_PRICE_INTERVAL);
                        _sessionStart = _lastBarCloseTime;

                        // Clear pending ticks from previous session
                        while (_pendingTicks.TryDequeue(out _)) { }
                    }
                    _lastSessionDate = currentBarDate;

                    Logger.Debug($"[NiftyFuturesMetricsService] OnRangeATRBarsUpdate(): New bar detected. Index: {e.MaxIndex}");

                    // Process pending ticks into the completed bar
                    ProcessPendingTicksIntoBar();

                    // Log the bar details (debug)
                    RangeBarLogger.LogBarCreated(_niftyFuturesSymbol, _lastBarCloseTime,
                        bars.GetOpen(e.MaxIndex), bars.GetHigh(e.MaxIndex), bars.GetLow(e.MaxIndex), bars.GetClose(e.MaxIndex),
                        bars.GetVolume(e.MaxIndex), e.MaxIndex);

                    // Set close price for HVN classification and recalculate metrics
                    _vpEngine.SetClosePrice(bars.GetClose(e.MaxIndex));
                    RecalculateAndPublishMetrics();

                    // Log bar with VP metrics in the requested format: BarTime, Close, SessionVAH, SessionVAL, SessionPOC, HVNs
                    RangeBarLogger.LogBarWithVP(_lastBarCloseTime, bars.GetClose(e.MaxIndex),
                        LatestMetrics.VAH, LatestMetrics.VAL, LatestMetrics.POC, LatestMetrics.HVNs);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] OnRangeATRBarsUpdate(): Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Processes pending live ticks into the VP engine.
        /// </summary>
        private void ProcessPendingTicksIntoBar()
        {
            var ticks = new List<LiveTick>();

            // Drain pending ticks
            while (_pendingTicks.TryDequeue(out var liveTick))
            {
                ticks.Add(liveTick);
            }

            if (ticks.Count == 0)
            {
                Logger.Debug("[NiftyFuturesMetricsService] ProcessPendingTicksIntoBar(): No pending ticks");
                return;
            }

            // Determine buy/sell for ticks based on price movement
            DetermineBuySellDirection(ticks);

            // Add ticks to VP engine
            foreach (var tick in ticks)
            {
                _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
            }

            Logger.Debug($"[NiftyFuturesMetricsService] ProcessPendingTicksIntoBar(): Processed {ticks.Count} ticks");
        }

        /// <summary>
        /// Determines buy/sell direction for ticks based on price movement.
        /// Simple approach: uptick = buy, downtick = sell.
        /// </summary>
        private void DetermineBuySellDirection(List<LiveTick> ticks)
        {
            if (ticks.Count == 0) return;

            double lastPrice = ticks[0].Price;

            foreach (var tick in ticks)
            {
                tick.IsBuy = tick.Price >= lastPrice;
                lastPrice = tick.Price;
            }
        }

        /// <summary>
        /// Handles tick bar updates (for accumulating live ticks).
        /// </summary>
        private void OnTickBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            if (!_isLivePhaseActive) return;

            // Extract new ticks from the update
            var bars = _tickBarsRequest.Bars;
            for (int i = e.MinIndex; i <= e.MaxIndex; i++)
            {
                _pendingTicks.Enqueue(new LiveTick
                {
                    Time = bars.GetTime(i),
                    Price = bars.GetClose(i),
                    Volume = bars.GetVolume(i),
                    IsBuy = bars.GetClose(i) >= bars.GetOpen(i)
                });
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // SESSION MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resets for a new trading session.
        /// Called when a new trading day starts.
        /// </summary>
        public void ResetSession()
        {
            Logger.Info("[NiftyFuturesMetricsService] ResetSession(): Resetting for new session...");

            _sessionStart = DateTime.Now;
            _vpEngine.Reset(VP_PRICE_INTERVAL);

            // Clear pending ticks
            while (_pendingTicks.TryDequeue(out _)) { }

            // Publish empty metrics
            var metrics = new NiftyFuturesVPMetrics
            {
                POC = 0,
                VAH = 0,
                VAL = 0,
                VWAP = 0,
                HVNs = new List<double>(),
                BarCount = 0,
                LastBarTime = DateTime.MinValue,
                LastUpdate = DateTime.Now,
                Symbol = _niftyFuturesSymbol,
                IsValid = false
            };

            LatestMetrics = metrics;
            _metricsSubject.OnNext(metrics);

            Logger.Info("[NiftyFuturesMetricsService] ResetSession(): Session reset complete");
        }

        // ═══════════════════════════════════════════════════════════════════
        // DISPOSAL
        // ═══════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Stop();

            _metricsSubject?.Dispose();
            _disposables?.Dispose();

            Logger.Info("[NiftyFuturesMetricsService] Dispose(): Service disposed");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SUPPORTING DATA STRUCTURES (Internal to avoid naming conflicts)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// RangeATR bar data container.
    /// </summary>
    internal class RangeATRBar
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
    internal class HistoricalTick
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
    internal class LiveTick
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

    /// <summary>
    /// Internal simplified Volume Profile Engine.
    /// Computes POC, VAH, VAL, VWAP, HVNs from tick data.
    /// Uses configurable price interval for price bucketing (1 rupee for NIFTY, not tick size).
    /// </summary>
    internal class InternalVolumeProfileEngine
    {
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;  // Price interval for VP buckets (1 rupee for NIFTY)
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;

        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _volumeAtPrice.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
        }

        public void AddTick(double price, long volume, bool isBuy)
        {
            if (price <= 0 || volume <= 0) return;

            double roundedPrice = Math.Round(price / _priceInterval) * _priceInterval;

            if (!_volumeAtPrice.ContainsKey(roundedPrice))
            {
                _volumeAtPrice[roundedPrice] = new VolumePriceLevel { Price = roundedPrice };
            }

            var level = _volumeAtPrice[roundedPrice];
            level.Volume += volume;
            if (isBuy)
                level.BuyVolume += volume;
            else
                level.SellVolume += volume;

            _totalVolume += volume;
            _sumPriceVolume += price * volume;
        }

        // Track last close price for HVN classification
        private double _lastClosePrice = 0;

        public void SetClosePrice(double closePrice)
        {
            _lastClosePrice = closePrice;
        }

        public InternalVPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new InternalVPResult();

            if (_volumeAtPrice.Count == 0 || _totalVolume == 0)
            {
                result.IsValid = false;
                return result;
            }

            // Find POC (price with highest volume)
            double maxVolume = 0;
            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume > maxVolume)
                {
                    maxVolume = level.Value.Volume;
                    result.POC = level.Key;
                }
            }

            // Calculate VWAP
            result.VWAP = _sumPriceVolume / _totalVolume;

            // Bidirectional expansion for Value Area
            var sortedLevels = _volumeAtPrice.OrderBy(l => l.Key).ToList();
            int pocIndex = sortedLevels.FindIndex(l => l.Key == result.POC);

            double targetVolume = _totalVolume * valueAreaPercent;
            double accumulatedVolume = maxVolume;

            int upperIndex = pocIndex;
            int lowerIndex = pocIndex;

            while (accumulatedVolume < targetVolume && (upperIndex < sortedLevels.Count - 1 || lowerIndex > 0))
            {
                double upperVolume = (upperIndex < sortedLevels.Count - 1) ?
                    sortedLevels[upperIndex + 1].Value.Volume : 0;
                double lowerVolume = (lowerIndex > 0) ?
                    sortedLevels[lowerIndex - 1].Value.Volume : 0;

                if (upperVolume >= lowerVolume && upperIndex < sortedLevels.Count - 1)
                {
                    upperIndex++;
                    accumulatedVolume += upperVolume;
                }
                else if (lowerIndex > 0)
                {
                    lowerIndex--;
                    accumulatedVolume += lowerVolume;
                }
                else
                {
                    break;
                }
            }

            result.VAH = sortedLevels[upperIndex].Key;
            result.VAL = sortedLevels[lowerIndex].Key;

            // Find HVNs (High Volume Nodes) and compute Buy/Sell breakdown
            // HVN classification uses price position relative to close price (like NinjaTrader):
            // - HVNs at or below close = BuyHVNs (support levels where buyers accumulated)
            // - HVNs above close = SellHVNs (resistance levels where sellers accumulated)
            double hvnThreshold = maxVolume * hvnRatio;
            result.HVNs = new List<double>();
            result.HVNBuyCount = 0;
            result.HVNSellCount = 0;
            result.HVNBuyVolume = 0;
            result.HVNSellVolume = 0;

            // Use last close price for HVN classification, fallback to POC if not set
            double classificationPrice = _lastClosePrice > 0 ? _lastClosePrice : result.POC;

            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume >= hvnThreshold)
                {
                    result.HVNs.Add(level.Key);

                    // Classify HVN based on price position relative to close
                    // HVNs at or below close = BuyHVNs (support)
                    // HVNs above close = SellHVNs (resistance)
                    if (level.Key <= classificationPrice)
                    {
                        result.HVNBuyCount++;
                        result.HVNBuyVolume += level.Value.Volume;
                    }
                    else
                    {
                        result.HVNSellCount++;
                        result.HVNSellVolume += level.Value.Volume;
                    }
                }
            }

            result.HVNs = result.HVNs.OrderBy(h => h).ToList();
            result.IsValid = true;

            return result;
        }
    }

    /// <summary>
    /// Volume at a price level with buy/sell breakdown.
    /// </summary>
    internal class VolumePriceLevel
    {
        public double Price { get; set; }
        public long Volume { get; set; }
        public long BuyVolume { get; set; }
        public long SellVolume { get; set; }
    }

    /// <summary>
    /// Result of VP calculation including HVN Buy/Sell breakdown.
    /// </summary>
    internal class InternalVPResult
    {
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public List<double> HVNs { get; set; } = new List<double>();
        public double ValueWidth => VAH - VAL;  // Value area width

        // HVN Buy/Sell counts - how many HVNs are dominated by buyers vs sellers
        public int HVNBuyCount { get; set; }    // HVNs where BuyVolume > SellVolume
        public int HVNSellCount { get; set; }   // HVNs where SellVolume > BuyVolume

        // Total buy/sell volumes across all HVN levels
        public long HVNBuyVolume { get; set; }
        public long HVNSellVolume { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Rolling Volume Profile Engine - maintains a time-windowed VP (60 minutes by default).
    /// Supports incremental updates with automatic expiration of old data.
    /// </summary>
    internal class RollingVolumeProfileEngine
    {
        private readonly Queue<RollingVolumeUpdate> _updates = new Queue<RollingVolumeUpdate>();
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;
        private int _rollingWindowMinutes = 60;
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;
        private double _lastClosePrice = 0;

        public RollingVolumeProfileEngine(int rollingWindowMinutes = 60)
        {
            _rollingWindowMinutes = rollingWindowMinutes;
        }

        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _volumeAtPrice.Clear();
            _updates.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
        }

        /// <summary>
        /// Adds a tick to the rolling VP with timestamp for expiration tracking.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            double roundedPrice = Math.Round(price / _priceInterval) * _priceInterval;

            // Store update for rolling window management
            _updates.Enqueue(new RollingVolumeUpdate
            {
                Price = roundedPrice,
                Volume = volume,
                IsBuy = isBuy,
                Time = tickTime
            });

            // Add to volume profile
            if (!_volumeAtPrice.ContainsKey(roundedPrice))
            {
                _volumeAtPrice[roundedPrice] = new VolumePriceLevel { Price = roundedPrice };
            }

            var level = _volumeAtPrice[roundedPrice];
            level.Volume += volume;
            if (isBuy)
                level.BuyVolume += volume;
            else
                level.SellVolume += volume;

            _totalVolume += volume;
            _sumPriceVolume += price * volume;
        }

        /// <summary>
        /// Removes expired data outside the rolling window.
        /// </summary>
        public void ExpireOldData(DateTime currentTime)
        {
            DateTime cutoffTime = currentTime.AddMinutes(-_rollingWindowMinutes);

            while (_updates.Count > 0 && _updates.Peek().Time < cutoffTime)
            {
                var oldUpdate = _updates.Dequeue();
                RemoveVolume(oldUpdate);
            }
        }

        private void RemoveVolume(RollingVolumeUpdate update)
        {
            if (!_volumeAtPrice.ContainsKey(update.Price)) return;

            var level = _volumeAtPrice[update.Price];
            level.Volume -= update.Volume;
            if (update.IsBuy)
                level.BuyVolume -= update.Volume;
            else
                level.SellVolume -= update.Volume;

            _totalVolume -= update.Volume;
            _sumPriceVolume -= update.Price * update.Volume;

            // Remove price level if empty
            if (level.Volume <= 0)
            {
                _volumeAtPrice.Remove(update.Price);
            }
        }

        public void SetClosePrice(double closePrice)
        {
            _lastClosePrice = closePrice;
        }

        public InternalVPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new InternalVPResult();

            if (_volumeAtPrice.Count == 0 || _totalVolume <= 0)
            {
                result.IsValid = false;
                return result;
            }

            // Find POC
            double maxVolume = 0;
            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume > maxVolume)
                {
                    maxVolume = level.Value.Volume;
                    result.POC = level.Key;
                }
            }

            // Calculate VWAP
            result.VWAP = _totalVolume > 0 ? _sumPriceVolume / _totalVolume : 0;

            // Bidirectional expansion for Value Area
            var sortedLevels = _volumeAtPrice.Where(l => l.Value.Volume > 0).OrderBy(l => l.Key).ToList();
            if (sortedLevels.Count == 0)
            {
                result.IsValid = false;
                return result;
            }

            int pocIndex = sortedLevels.FindIndex(l => l.Key == result.POC);
            if (pocIndex < 0) pocIndex = 0;

            double targetVolume = _totalVolume * valueAreaPercent;
            double accumulatedVolume = maxVolume;

            int upperIndex = pocIndex;
            int lowerIndex = pocIndex;

            while (accumulatedVolume < targetVolume && (upperIndex < sortedLevels.Count - 1 || lowerIndex > 0))
            {
                double upperVolume = (upperIndex < sortedLevels.Count - 1) ?
                    sortedLevels[upperIndex + 1].Value.Volume : 0;
                double lowerVolume = (lowerIndex > 0) ?
                    sortedLevels[lowerIndex - 1].Value.Volume : 0;

                if (upperVolume >= lowerVolume && upperIndex < sortedLevels.Count - 1)
                {
                    upperIndex++;
                    accumulatedVolume += upperVolume;
                }
                else if (lowerIndex > 0)
                {
                    lowerIndex--;
                    accumulatedVolume += lowerVolume;
                }
                else
                {
                    break;
                }
            }

            result.VAH = sortedLevels[upperIndex].Key;
            result.VAL = sortedLevels[lowerIndex].Key;

            // Calculate HVNs
            double hvnThreshold = maxVolume * hvnRatio;
            result.HVNs = new List<double>();
            result.HVNBuyCount = 0;
            result.HVNSellCount = 0;
            result.HVNBuyVolume = 0;
            result.HVNSellVolume = 0;

            double classificationPrice = _lastClosePrice > 0 ? _lastClosePrice : result.POC;

            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume >= hvnThreshold)
                {
                    result.HVNs.Add(level.Key);

                    if (level.Key <= classificationPrice)
                    {
                        result.HVNBuyCount++;
                        result.HVNBuyVolume += level.Value.Volume;
                    }
                    else
                    {
                        result.HVNSellCount++;
                        result.HVNSellVolume += level.Value.Volume;
                    }
                }
            }

            result.HVNs = result.HVNs.OrderBy(h => h).ToList();
            result.IsValid = true;

            return result;
        }
    }

    /// <summary>
    /// Stores a volume update for rolling window expiration.
    /// </summary>
    internal class RollingVolumeUpdate
    {
        public double Price { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// CircularBuffer with O(1) insertion at front. [0] = most recent value.
    /// </summary>
    internal class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _head = 0;
            _count = 0;
        }

        public void Add(T item)
        {
            _head = (_head - 1 + _buffer.Length) % _buffer.Length;
            _buffer[_head] = item;
            if (_count < _buffer.Length) _count++;
        }

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
                return _buffer[(_head + index) % _buffer.Length];
            }
        }

        public int Count => _count;

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Relative metrics engine for VP HVN Buy/Sell.
    /// Tracks time-indexed historical averages and computes Rel/Cum metrics.
    /// Supports both Session VP and Rolling VP metrics.
    ///
    /// Architecture matches NinjaTrader RelMetricsNF:
    /// - Historical averages are built during historical data processing (UpdateHistory)
    /// - Current session metrics are accumulated separately (Update)
    /// - Cumulative = sum(current values through session) / sum(reference values through session) * 100
    /// </summary>
    internal class VPRelativeMetricsEngine
    {
        // Configuration
        private const int LOOKBACK_DAYS = 10;       // Days of history for averaging
        private const int MAX_BUFFER_SIZE = 256;    // CircularBuffer capacity
        private const int WARMUP_SECONDS = 15;      // Skip first 15 seconds of session (like NinjaTrader)

        // Time-indexed historical storage (1440 minutes per day)
        // Session: [idx][0]=HVNBuyCount, [1]=HVNSellCount, [2]=ValueWidth
        // Rolling: [idx][0]=RollingHVNBuy, [1]=RollingHVNSell, [2]=RollingValueWidth
        private readonly Dictionary<int, double[]> _avgByTime = new Dictionary<int, double[]>();       // Session averages
        private readonly Dictionary<int, Queue<double>[]> _history = new Dictionary<int, Queue<double>[]>();
        private readonly Dictionary<int, double[]> _rollingAvgByTime = new Dictionary<int, double[]>(); // Rolling averages
        private readonly Dictionary<int, Queue<double>[]> _rollingHistory = new Dictionary<int, Queue<double>[]>();

        // Session cumulative tracking (for Session VP)
        private double[] _sessionCumul = new double[3];  // HVNBuy, HVNSell, ValueWidth
        private double[] _sessionRef = new double[3];
        private DateTime _sessionDate = DateTime.MinValue;
        private DateTime _sessionStartTime = DateTime.MinValue;
        private int _sessionBarCount = 0;

        // Rolling cumulative tracking (for Rolling VP)
        private double[] _rollingCumul = new double[3];  // RollingHVNBuy, RollingHVNSell, RollingValueWidth
        private double[] _rollingRef = new double[3];

        // Session VP Result buffers
        public CircularBuffer<double> RelHVNBuy { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelHVNSell { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelValueWidth { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNBuyRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNSellRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumValueWidthRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);

        // Rolling VP Result buffers
        public CircularBuffer<double> RelHVNBuyRolling { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelHVNSellRolling { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelValueWidthRolling { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNBuyRollingRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNSellRollingRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumValueWidthRollingRank { get; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);

        public VPRelativeMetricsEngine()
        {
            // Initialize time-indexed storage for all 1440 minutes
            for (int i = 0; i < 1440; i++)
            {
                // Session history
                _avgByTime[i] = new double[3];
                _history[i] = new Queue<double>[3];
                for (int j = 0; j < 3; j++)
                    _history[i][j] = new Queue<double>();

                // Rolling history
                _rollingAvgByTime[i] = new double[3];
                _rollingHistory[i] = new Queue<double>[3];
                for (int j = 0; j < 3; j++)
                    _rollingHistory[i][j] = new Queue<double>();
            }
        }

        /// <summary>
        /// Update historical averages from bar data (called during historical processing).
        /// This builds the reference data that relative metrics are computed against.
        /// </summary>
        public void UpdateHistory(DateTime time, double hvnBuyCount, double hvnSellCount, double valueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;
            double[] vals = new double[] { hvnBuyCount, hvnSellCount, valueWidth };

            for (int i = 0; i < 3; i++)
            {
                Queue<double> q = _history[idx][i];
                if (q.Count >= LOOKBACK_DAYS) q.Dequeue();
                q.Enqueue(vals[i]);
                _avgByTime[idx][i] = q.Count > 0 ? q.Average() : vals[i];
            }
        }

        /// <summary>
        /// Update historical averages for Rolling VP metrics.
        /// Called during historical processing to build rolling VP reference data.
        /// </summary>
        public void UpdateRollingHistory(DateTime time, double rollingHVNBuy, double rollingHVNSell, double rollingValueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;
            double[] vals = new double[] { rollingHVNBuy, rollingHVNSell, rollingValueWidth };

            for (int i = 0; i < 3; i++)
            {
                Queue<double> q = _rollingHistory[idx][i];
                if (q.Count >= LOOKBACK_DAYS) q.Dequeue();
                q.Enqueue(vals[i]);
                _rollingAvgByTime[idx][i] = q.Count > 0 ? q.Average() : vals[i];
            }
        }

        /// <summary>
        /// Start a new session for cumulative tracking.
        /// Call this at the start of a trading day before calling Update.
        /// </summary>
        public void StartSession(DateTime sessionStart)
        {
            _sessionDate = sessionStart.Date;
            _sessionStartTime = sessionStart;
            _sessionBarCount = 0;
            for (int i = 0; i < 3; i++)
            {
                _sessionCumul[i] = 0;
                _sessionRef[i] = 0;
                _rollingCumul[i] = 0;
                _rollingRef[i] = 0;
            }

            // Clear Session VP result buffers
            RelHVNBuy.Clear();
            RelHVNSell.Clear();
            RelValueWidth.Clear();
            CumHVNBuyRank.Clear();
            CumHVNSellRank.Clear();
            CumValueWidthRank.Clear();

            // Clear Rolling VP result buffers
            RelHVNBuyRolling.Clear();
            RelHVNSellRolling.Clear();
            RelValueWidthRolling.Clear();
            CumHVNBuyRollingRank.Clear();
            CumHVNSellRollingRank.Clear();
            CumValueWidthRollingRank.Clear();
        }

        /// <summary>
        /// Calculate relative and cumulative metrics for current bar.
        /// This accumulates session values for proper cumulative calculation.
        /// </summary>
        public void Update(DateTime time, double hvnBuyCount, double hvnSellCount, double valueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;

            // Check for new session (auto-detect if StartSession wasn't called)
            if (time.Date != _sessionDate)
            {
                StartSession(time);
            }

            // Get reference values (historical averages at this time of day)
            double[] reference = GetReferenceMetrics(idx);
            double[] current = new double[] { hvnBuyCount, hvnSellCount, valueWidth };

            // Use time-based warmup like NinjaTrader RelMetricsNF
            bool isWarmup = _sessionStartTime != DateTime.MinValue &&
                           (time - _sessionStartTime).TotalSeconds < WARMUP_SECONDS;

            // Calculate relative values: current / reference * 100
            double[] relativeValues = new double[3];
            double[] cumulativeValues = new double[3];

            for (int i = 0; i < 3; i++)
            {
                // Relative: current / avgAtTime * 100
                if (reference[i] > 0 && current[i] > 0)
                    relativeValues[i] = (current[i] / reference[i]) * 100;
                else
                    relativeValues[i] = 0;

                // Cumulative: sum(current) / sum(reference) * 100
                if (!isWarmup)
                {
                    if (reference[i] > 0 && current[i] > 0)
                    {
                        // Accumulate session totals
                        _sessionCumul[i] += current[i];
                        _sessionRef[i] += reference[i];

                        // Calculate cumulative rank
                        if (_sessionRef[i] > 0)
                            cumulativeValues[i] = (_sessionCumul[i] / _sessionRef[i]) * 100;
                        else
                            cumulativeValues[i] = 100;
                    }
                    else if (_sessionBarCount > 0)
                    {
                        // If current or reference is 0, use previous cumulative value
                        cumulativeValues[i] = GetPreviousCumulativeValue(i);
                    }
                    else
                    {
                        cumulativeValues[i] = 100;
                    }
                }
                else
                {
                    cumulativeValues[i] = 100;
                }
            }

            // Store in circular buffers
            RelHVNBuy.Add(Math.Round(relativeValues[0], 2));
            RelHVNSell.Add(Math.Round(relativeValues[1], 2));
            RelValueWidth.Add(Math.Round(relativeValues[2], 2));
            CumHVNBuyRank.Add(Math.Round(cumulativeValues[0], 2));
            CumHVNSellRank.Add(Math.Round(cumulativeValues[1], 2));
            CumValueWidthRank.Add(Math.Round(cumulativeValues[2], 2));

            _sessionBarCount++;
        }

        /// <summary>
        /// Get the current session's cumulative totals for debugging.
        /// </summary>
        public (double cumHVNBuy, double refHVNBuy, double cumHVNSell, double refHVNSell, double cumValWidth, double refValWidth) GetSessionTotals()
        {
            return (_sessionCumul[0], _sessionRef[0], _sessionCumul[1], _sessionRef[1], _sessionCumul[2], _sessionRef[2]);
        }

        private double[] GetReferenceMetrics(int idx)
        {
            double[] result = new double[3];
            int[] windowSizes = new int[] { 10, 30, 60 };

            for (int i = 0; i < 3; i++)
            {
                result[i] = GetWeightedReference(idx, i, windowSizes);
            }

            return result;
        }

        private double GetWeightedReference(int timeIdx, int dataIndex, int[] windowSizes)
        {
            // 1. Exact Match
            if (_avgByTime.ContainsKey(timeIdx) && _avgByTime[timeIdx][dataIndex] > 0)
                return _avgByTime[timeIdx][dataIndex];

            // 2. Window Search (weighted by proximity)
            foreach (int windowSize in windowSizes)
            {
                double totalWeight = 0;
                double weightedSum = 0;
                for (int offset = -windowSize; offset <= windowSize; offset++)
                {
                    int tIdx = (timeIdx + offset + 1440) % 1440;
                    if (_avgByTime.ContainsKey(tIdx) && _avgByTime[tIdx][dataIndex] > 0)
                    {
                        double weight = 1.0 / (Math.Abs(offset) + 1);
                        weightedSum += _avgByTime[tIdx][dataIndex] * weight;
                        totalWeight += weight;
                    }
                }
                if (totalWeight > 0) return weightedSum / totalWeight;
            }

            // 3. Session Average fallback
            double sum = 0;
            int count = 0;
            foreach (var kvp in _avgByTime)
            {
                if (kvp.Value[dataIndex] > 0) { sum += kvp.Value[dataIndex]; count++; }
            }
            return count > 0 ? sum / count : 0;
        }

        private double GetPreviousCumulativeValue(int index)
        {
            return index switch
            {
                0 => CumHVNBuyRank.Count > 0 ? CumHVNBuyRank[0] : 100,
                1 => CumHVNSellRank.Count > 0 ? CumHVNSellRank[0] : 100,
                2 => CumValueWidthRank.Count > 0 ? CumValueWidthRank[0] : 100,
                _ => 100
            };
        }

        /// <summary>
        /// Calculate relative and cumulative metrics for Rolling VP.
        /// Call this alongside Update() with rolling VP metrics.
        /// </summary>
        public void UpdateRolling(DateTime time, double rollingHVNBuy, double rollingHVNSell, double rollingValueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;

            // Get reference values for rolling metrics
            double[] reference = GetRollingReferenceMetrics(idx);
            double[] current = new double[] { rollingHVNBuy, rollingHVNSell, rollingValueWidth };

            // Use time-based warmup
            bool isWarmup = _sessionStartTime != DateTime.MinValue &&
                           (time - _sessionStartTime).TotalSeconds < WARMUP_SECONDS;

            // Calculate relative values: current / reference * 100
            double[] relativeValues = new double[3];
            double[] cumulativeValues = new double[3];

            for (int i = 0; i < 3; i++)
            {
                // Relative: current / avgAtTime * 100
                if (reference[i] > 0 && current[i] > 0)
                    relativeValues[i] = (current[i] / reference[i]) * 100;
                else
                    relativeValues[i] = 0;

                // Cumulative: sum(current) / sum(reference) * 100
                if (!isWarmup)
                {
                    if (reference[i] > 0 && current[i] > 0)
                    {
                        // Accumulate rolling totals
                        _rollingCumul[i] += current[i];
                        _rollingRef[i] += reference[i];

                        // Calculate cumulative rank
                        if (_rollingRef[i] > 0)
                            cumulativeValues[i] = (_rollingCumul[i] / _rollingRef[i]) * 100;
                        else
                            cumulativeValues[i] = 100;
                    }
                    else if (_sessionBarCount > 0)
                    {
                        cumulativeValues[i] = GetPreviousRollingCumulativeValue(i);
                    }
                    else
                    {
                        cumulativeValues[i] = 100;
                    }
                }
                else
                {
                    cumulativeValues[i] = 100;
                }
            }

            // Store in rolling circular buffers
            RelHVNBuyRolling.Add(Math.Round(relativeValues[0], 2));
            RelHVNSellRolling.Add(Math.Round(relativeValues[1], 2));
            RelValueWidthRolling.Add(Math.Round(relativeValues[2], 2));
            CumHVNBuyRollingRank.Add(Math.Round(cumulativeValues[0], 2));
            CumHVNSellRollingRank.Add(Math.Round(cumulativeValues[1], 2));
            CumValueWidthRollingRank.Add(Math.Round(cumulativeValues[2], 2));
        }

        private double[] GetRollingReferenceMetrics(int idx)
        {
            double[] result = new double[3];
            int[] windowSizes = new int[] { 10, 30, 60 };

            for (int i = 0; i < 3; i++)
            {
                result[i] = GetWeightedRollingReference(idx, i, windowSizes);
            }

            return result;
        }

        private double GetWeightedRollingReference(int timeIdx, int dataIndex, int[] windowSizes)
        {
            // 1. Exact Match
            if (_rollingAvgByTime.ContainsKey(timeIdx) && _rollingAvgByTime[timeIdx][dataIndex] > 0)
                return _rollingAvgByTime[timeIdx][dataIndex];

            // 2. Window Search (weighted by proximity)
            foreach (int windowSize in windowSizes)
            {
                double totalWeight = 0;
                double weightedSum = 0;
                for (int offset = -windowSize; offset <= windowSize; offset++)
                {
                    int tIdx = (timeIdx + offset + 1440) % 1440;
                    if (_rollingAvgByTime.ContainsKey(tIdx) && _rollingAvgByTime[tIdx][dataIndex] > 0)
                    {
                        double weight = 1.0 / (Math.Abs(offset) + 1);
                        weightedSum += _rollingAvgByTime[tIdx][dataIndex] * weight;
                        totalWeight += weight;
                    }
                }
                if (totalWeight > 0) return weightedSum / totalWeight;
            }

            // 3. Session Average fallback
            double sum = 0;
            int count = 0;
            foreach (var kvp in _rollingAvgByTime)
            {
                if (kvp.Value[dataIndex] > 0) { sum += kvp.Value[dataIndex]; count++; }
            }
            return count > 0 ? sum / count : 0;
        }

        private double GetPreviousRollingCumulativeValue(int index)
        {
            return index switch
            {
                0 => CumHVNBuyRollingRank.Count > 0 ? CumHVNBuyRollingRank[0] : 100,
                1 => CumHVNSellRollingRank.Count > 0 ? CumHVNSellRollingRank[0] : 100,
                2 => CumValueWidthRollingRank.Count > 0 ? CumValueWidthRollingRank[0] : 100,
                _ => 100
            };
        }
    }

    /// <summary>
    /// Efficient tick-to-bar mapper using index ranges.
    /// Provides O(1) lookup for ticks within a RangeATR bar.
    /// </summary>
    internal class InternalTickToBarMapper
    {
        private readonly List<RangeATRBar> _bars;
        private readonly List<HistoricalTick> _ticks;

        // Maps bar index to (startTickIndex, endTickIndex)
        private readonly Dictionary<int, (int start, int end)> _barToTickRange;

        public int MappedBarsCount => _barToTickRange.Count;

        public InternalTickToBarMapper(List<RangeATRBar> bars, List<HistoricalTick> ticks)
        {
            _bars = bars ?? new List<RangeATRBar>();
            _ticks = ticks ?? new List<HistoricalTick>();
            _barToTickRange = new Dictionary<int, (int start, int end)>();
        }

        /// <summary>
        /// Builds the tick-to-bar index mapping.
        /// </summary>
        public void BuildIndex()
        {
            if (_bars.Count == 0 || _ticks.Count == 0)
                return;

            for (int i = 0; i < _bars.Count; i++)
            {
                var currentBar = _bars[i];

                DateTime barStartTime;
                DateTime barEndTime = currentBar.Time;

                if (i > 0)
                {
                    barStartTime = _bars[i - 1].Time;
                }
                else
                {
                    barStartTime = currentBar.Time.AddMinutes(-5);
                }

                int startIdx = BinarySearchTickIndex(barStartTime, true);
                int endIdx = BinarySearchTickIndex(barEndTime, false);

                if (startIdx >= 0 && endIdx >= 0 && startIdx <= endIdx)
                {
                    _barToTickRange[currentBar.Index] = (startIdx, endIdx);
                }
            }
        }

        private int BinarySearchTickIndex(DateTime targetTime, bool findFirst)
        {
            if (_ticks.Count == 0) return -1;

            int left = 0;
            int right = _ticks.Count - 1;
            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var midTime = _ticks[mid].Time;

                if (midTime == targetTime)
                {
                    result = mid;
                    if (findFirst)
                        right = mid - 1;
                    else
                        left = mid + 1;
                }
                else if (midTime < targetTime)
                {
                    if (!findFirst) result = mid;
                    left = mid + 1;
                }
                else
                {
                    if (findFirst) result = mid;
                    right = mid - 1;
                }
            }

            if (result == -1)
            {
                result = findFirst ? left : right;
                result = Math.Max(0, Math.Min(result, _ticks.Count - 1));
            }

            return result;
        }

        /// <summary>
        /// Gets all ticks that belong to a specific bar.
        /// </summary>
        public List<HistoricalTick> GetTicksForBar(int barIndex)
        {
            if (!_barToTickRange.TryGetValue(barIndex, out var range))
                return new List<HistoricalTick>();

            var result = new List<HistoricalTick>(range.end - range.start + 1);

            for (int i = range.start; i <= range.end && i < _ticks.Count; i++)
            {
                result.Add(_ticks[i]);
            }

            return result;
        }
    }
}
