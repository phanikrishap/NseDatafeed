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
        private const double HVN_RATIO = 0.70;          // HVN threshold (70% of POC volume)

        // ═══════════════════════════════════════════════════════════════════
        // STATE
        // ═══════════════════════════════════════════════════════════════════

        // NinjaTrader objects
        private BarsRequest _rangeATRBarsRequest;
        private BarsRequest _tickBarsRequest;
        private Instrument _niftyFuturesInstrument;  // Zerodha symbol (NIFTY26JANFUT) for live WebSocket mapping
        private Instrument _niftyIInstrument;        // NT continuous symbol (NIFTY_I) for BarsRequest
        private string _niftyFuturesSymbol;          // Zerodha trading symbol (for logging/display)
        private double _tickSize = 0.05; // Default for NIFTY, will be updated from instrument

        // Internal Volume Profile Engine (simplified)
        private readonly InternalVolumeProfileEngine _vpEngine = new InternalVolumeProfileEngine();

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
                Logger.Info($"[NiftyFuturesMetricsService] StartAsync(): Got {NIFTY_I_SYMBOL} instrument, tickSize={_tickSize}");
                RangeBarLogger.Info($"[INSTRUMENT] {NIFTY_I_SYMBOL} tickSize={_tickSize}");

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
            });
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

            // Process each session
            foreach (var sessionGroup in sessionGroups)
            {
                DateTime sessionDate = sessionGroup.Key;
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();

                // Reset VP engine for new session
                _vpEngine.Reset(_tickSize);
                _sessionStart = sessionBars.First().Time;

                // Log session start at DEBUG level
                RangeBarLogger.Debug($"[SESSION] {sessionDate:yyyy-MM-dd} | Bars={sessionBars.Count} | Start={_sessionStart:HH:mm:ss}");

                int sessionBarIndex = 0;
                foreach (var bar in sessionBars)
                {
                    // Get ticks for this bar
                    var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);

                    if (ticksForBar.Count > 0)
                    {
                        // Add ticks to VP engine
                        foreach (var tick in ticksForBar)
                        {
                            _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                        }
                        totalBarsWithTicks++;
                    }

                    sessionBarIndex++;
                    totalProcessedBars++;

                    // Calculate current VP metrics after this bar
                    var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                    // Log bar-by-bar VP evolution at DEBUG level
                    // Format: BarTime, Close, SessionVAH, SessionVAL, SessionPOC, HVNs
                    if (vpMetrics.IsValid)
                    {
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

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Processed {totalProcessedBars} bars across {sessionGroups.Count} sessions, {totalBarsWithTicks} with tick data");

            // Set _lastSessionDate for live session detection
            _lastSessionDate = sessionGroups.Last().Key;

            // For final metrics, we want TODAY's session (or last session if today has no data)
            var todaysBars = _historicalRangeATRBars.Where(b => b.Time.Date == DateTime.Today).ToList();
            if (todaysBars.Count > 0)
            {
                // Re-process today's session for final state
                _vpEngine.Reset(_tickSize);
                _sessionStart = todaysBars.First().Time;

                foreach (var bar in todaysBars)
                {
                    var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);
                    foreach (var tick in ticksForBar)
                    {
                        _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                    }
                }

                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from today's {todaysBars.Count} bars");
            }
            else
            {
                // Use the last available session
                var lastSession = sessionGroups.Last();
                _vpEngine.Reset(_tickSize);
                _sessionStart = lastSession.First().Time;

                foreach (var bar in lastSession)
                {
                    var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);
                    foreach (var tick in ticksForBar)
                    {
                        _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                    }
                }

                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from last session {lastSession.Key:yyyy-MM-dd} ({lastSession.Count()} bars)");
            }

            // Calculate and publish initial metrics
            RecalculateAndPublishMetrics();
        }

        /// <summary>
        /// Recalculates VP metrics and publishes to stream.
        /// </summary>
        private void RecalculateAndPublishMetrics()
        {
            try
            {
                // Calculate VP metrics from internal engine
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                var metrics = new NiftyFuturesVPMetrics
                {
                    POC = vpMetrics.POC,
                    VAH = vpMetrics.VAH,
                    VAL = vpMetrics.VAL,
                    VWAP = vpMetrics.VWAP,
                    HVNs = vpMetrics.HVNs,
                    BarCount = _historicalRangeATRBars.Count(b => b.Time >= _sessionStart),
                    LastBarTime = _lastBarCloseTime,
                    LastUpdate = DateTime.Now,
                    Symbol = _niftyFuturesSymbol,
                    IsValid = vpMetrics.IsValid
                };

                LatestMetrics = metrics;
                _metricsSubject.OnNext(metrics);

                Logger.Info($"[NiftyFuturesMetricsService] Metrics: POC={metrics.POC:F2}, VAH={metrics.VAH:F2}, VAL={metrics.VAL:F2}, VWAP={metrics.VWAP:F2}, HVNs={metrics.HVNs?.Count ?? 0}, Bars={metrics.BarCount}");
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

                        _vpEngine.Reset(_tickSize);
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

                    // Recalculate and publish metrics
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
            _vpEngine.Reset(_tickSize);

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
    /// Nifty Futures VP metrics snapshot.
    /// </summary>
    public class NiftyFuturesVPMetrics
    {
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public List<double> HVNs { get; set; } = new List<double>();
        public int BarCount { get; set; }
        public DateTime LastBarTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public string Symbol { get; set; }
        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Internal simplified Volume Profile Engine.
    /// Computes POC, VAH, VAL, VWAP, HVNs from tick data.
    /// </summary>
    internal class InternalVolumeProfileEngine
    {
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _tickSize = 0.05;
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;

        public void Reset(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.05;
            _volumeAtPrice.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
        }

        public void AddTick(double price, long volume, bool isBuy)
        {
            if (price <= 0 || volume <= 0) return;

            double roundedPrice = Math.Round(price / _tickSize) * _tickSize;

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

            // Find HVNs (High Volume Nodes)
            double hvnThreshold = maxVolume * hvnRatio;
            result.HVNs = new List<double>();

            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume >= hvnThreshold)
                {
                    result.HVNs.Add(level.Key);
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
    /// Result of VP calculation.
    /// </summary>
    internal class InternalVPResult
    {
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public List<double> HVNs { get; set; } = new List<double>();
        public bool IsValid { get; set; }
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
