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
using ZerodhaDatafeedAdapter.Services.Analysis.Components; // Import the new components

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Service that computes Volume Profile metrics for NIFTY Futures using RangeATR bars.
    /// Refactored to use modular components.
    /// </summary>
    public class NiftyFuturesMetricsService : IDisposable
    {
        private static readonly Lazy<NiftyFuturesMetricsService> _instance =
            new Lazy<NiftyFuturesMetricsService>(() => new NiftyFuturesMetricsService());
        public static NiftyFuturesMetricsService Instance => _instance.Value;

        // NinjaTrader continuous contract symbol for NIFTY Futures
        private const string NIFTY_I_SYMBOL = "NIFTY_I";

        // Configuration
        private const int HISTORICAL_DAYS = 40;
        private const int YEARLY_BARS = 252; // 1 year of daily bars for yearly high/low
        private const double VALUE_AREA_PERCENT = 0.70;
        private const double HVN_RATIO = 0.25;
        private const double VP_PRICE_INTERVAL = 1.0;
        private const int RANGE_ATR_BARS_TYPE = 7015;
        private const int RANGE_ATR_MINUTE_VALUE = 1;
        private const int RANGE_ATR_MIN_SECONDS = 3;
        private const int RANGE_ATR_MIN_TICKS = 1;

        // Dependencies
        private readonly NiftySymbolResolver _symbolResolver;
        private readonly NiftyHistoricalDataLoader _historicalDataLoader;
        private readonly MetricsCalculator _metricsCalculator;

        // State
        private BarsRequest _rangeATRBarsRequest;
        private BarsRequest _tickBarsRequest;
        private BarsRequest _minuteBarsRequest;
        private BarsRequest _dailyBarsRequest; // 1440-minute bars for yearly high/low and ADR
        private Instrument _niftyFuturesInstrument;
        private Instrument _niftyIInstrument;
        private string _niftyFuturesSymbol;
        private double _tickSize = 0.05;

        // Components
        private readonly VPEngine _vpEngine = new VPEngine();
        private readonly RollingVolumeProfileEngine _rollingVpEngine = new RollingVolumeProfileEngine(60);
        private readonly VPRelativeMetricsEngine _relMetrics = new VPRelativeMetricsEngine();
        private CompositeProfileEngine _compositeEngine = new CompositeProfileEngine();
        
        // Replaced _tickMapper with sequential index tracking
        private int _simTickIndex = 0;

        private DateTime _lastMinuteBarTime = DateTime.MinValue;
        private double _currentDayHigh = double.MinValue;
        private double _currentDayLow = double.MaxValue;
        private double _lastPrice = 0;

        // Data
        private List<RangeATRBar> _historicalRangeATRBars = new List<RangeATRBar>();
        private List<HistoricalTick> _historicalTicks = new List<HistoricalTick>();
        private readonly ConcurrentQueue<LiveTick> _pendingTicks = new ConcurrentQueue<LiveTick>();

        private DateTime _lastBarCloseTime = DateTime.MinValue;
        private int _lastBarIndex = -1;
        private DateTime _sessionStart = DateTime.MinValue;
        private DateTime _lastSessionDate = DateTime.MinValue;
        private bool _isLivePhaseActive = false;

        // Simulation replay state
        private List<(DateTime BarTime, double ClosePrice, int BarIndex)> _simReplayBars;
        private List<NiftyFuturesVPMetrics> _simPrecomputedMetrics = new List<NiftyFuturesVPMetrics>();
        private int _simReplayBarIndex = 0;
        private bool _simDataReady = false;
        private IDisposable _simTickSubscription;

        // Reactive streams
        private readonly BehaviorSubject<NiftyFuturesVPMetrics> _metricsSubject =
            new BehaviorSubject<NiftyFuturesVPMetrics>(new NiftyFuturesVPMetrics { IsValid = false });
        public IObservable<NiftyFuturesVPMetrics> MetricsStream => _metricsSubject.AsObservable();
        public NiftyFuturesVPMetrics LatestMetrics { get; private set; } = new NiftyFuturesVPMetrics { IsValid = false };

        private readonly System.Reactive.Disposables.CompositeDisposable _disposables =
            new System.Reactive.Disposables.CompositeDisposable();
        private IDisposable _tickSubscription;
        private int _disposed = 0;

        private NiftyFuturesMetricsService()
        {
            Logger.Info("[NiftyFuturesMetricsService] Constructor: Initializing singleton instance");
            RangeBarLogger.Info("[NiftyFuturesMetricsService] Initializing - RangeBar logging started");

            // Initialize components
            _symbolResolver = new NiftySymbolResolver();
            _historicalDataLoader = new NiftyHistoricalDataLoader();
            _metricsCalculator = new MetricsCalculator();
        }

        public async Task StartAsync()
        {
            Logger.Info("[NiftyFuturesMetricsService] StartAsync(): Starting service...");

            try
            {
                var dbReady = await MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                    .Timeout(TimeSpan.FromSeconds(90))
                    .FirstAsync()
                    .ToTask();

                if (!dbReady)
                {
                    Logger.Error("[NiftyFuturesMetricsService] StartAsync(): Instrument DB not ready");
                    return;
                }

                _niftyFuturesSymbol = await _symbolResolver.ResolveNiftyFuturesSymbolAsync();
                if (string.IsNullOrEmpty(_niftyFuturesSymbol))
                {
                    Logger.Error("[NiftyFuturesMetricsService] StartAsync(): Failed to resolve NIFTY Futures symbol");
                    return;
                }

                RangeBarLogger.Info($"[SERVICE_START] Symbol resolved: {_niftyFuturesSymbol}");

                // Get Instruments
                _niftyIInstrument = await _symbolResolver.GetInstrumentAsync(NIFTY_I_SYMBOL);
                _niftyFuturesInstrument = await _symbolResolver.GetInstrumentAsync(_niftyFuturesSymbol);

                if (_niftyIInstrument == null)
                {
                    Logger.Error($"[NiftyFuturesMetricsService] StartAsync(): Failed to get NT instrument for {NIFTY_I_SYMBOL}");
                    return;
                }

                _tickSize = _niftyIInstrument.MasterInstrument.TickSize;
                Logger.Info($"[NiftyFuturesMetricsService] StartAsync(): Got {NIFTY_I_SYMBOL} instrument, tickSize={_tickSize}, VP priceInterval={VP_PRICE_INTERVAL}");

                // Request Historical Data
                await RequestHistoricalData();

                // Subscribe Live
                SubscribeToLiveTicks();

                Logger.Info("[NiftyFuturesMetricsService] StartAsync(): Service started successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] StartAsync(): Exception - {ex.Message}", ex);
            }
        }

        public void Stop()
        {
            Logger.Info("[NiftyFuturesMetricsService] Stop(): Stopping service...");
            _isLivePhaseActive = false;
            _simDataReady = false;
            _tickSubscription?.Dispose();
            _simTickSubscription?.Dispose();

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

                if (_minuteBarsRequest != null)
                {
                    _minuteBarsRequest.Update -= OnMinuteBarsUpdate;
                    _minuteBarsRequest.Dispose();
                    _minuteBarsRequest = null;
                }

                if (_dailyBarsRequest != null)
                {
                    _dailyBarsRequest.Update -= OnDailyBarsUpdate;
                    _dailyBarsRequest.Dispose();
                    _dailyBarsRequest = null;
                }
            });
            Logger.Info("[NiftyFuturesMetricsService] Stop(): Service stopped");
        }

        /// <summary>
        /// Loads historical data for simulation mode - up to the simulation date.
        /// This resets VP engines, loads prior days for historical context, then builds
        /// replay data for the simulation date itself for progressive bar-by-bar replay.
        /// NOTE: Always uses NIFTY_I for volume profile metrics, regardless of whether
        /// simulating NIFTY or SENSEX options (SENSEX_I is illiquid and NIFTY/SENSEX are complementary).
        /// </summary>
        /// <param name="simulationDate">The date to simulate</param>
        public async Task StartSimulationAsync(DateTime simulationDate)
        {
            // Always use NIFTY_I for volume profile - it's the liquid underlying
            // SENSEX_I is illiquid; NIFTY and SENSEX are highly complementary instruments
            string continuousSymbol = NIFTY_I_SYMBOL;

            Logger.Info($"[NiftyFuturesMetricsService] StartSimulationAsync(): Loading data for simulation date {simulationDate:yyyy-MM-dd}, symbol={continuousSymbol}");
            LoggerFactory.Simulation.Info($"[NiftyFuturesMetricsService] StartSimulationAsync: {simulationDate:yyyy-MM-dd}");

            try
            {
                // Stop live updates
                _isLivePhaseActive = false;
                _tickSubscription?.Dispose();
                _simTickSubscription?.Dispose();

                // Reset VP engines
                _vpEngine.Reset(VP_PRICE_INTERVAL);
                _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
                _compositeEngine.Reset();
                _historicalRangeATRBars.Clear();
                _historicalTicks.Clear();
                _simPrecomputedMetrics.Clear();
                _sessionStart = DateTime.MinValue;
                _lastSessionDate = DateTime.MinValue;
                _simDataReady = false;
                _simReplayBarIndex = 0;

                // Get the appropriate continuous contract instrument
                Instrument simInstrument = await _symbolResolver.GetInstrumentAsync(continuousSymbol);

                if (simInstrument == null)
                {
                    Logger.Error($"[NiftyFuturesMetricsService] StartSimulationAsync: Failed to get {continuousSymbol} instrument");
                    LoggerFactory.Simulation.Error($"[NiftyFuturesMetricsService] Failed to get instrument for {continuousSymbol}");
                    return;
                }

                Logger.Info($"[NiftyFuturesMetricsService] StartSimulationAsync: Got instrument {simInstrument.FullName} for {continuousSymbol}");

                // Load historical data up to simulation date (prior day's close, not the simulation day itself)
                // This populates the circular buffers for RelMetrics historical averages
                var historyEndDate = simulationDate.Date.AddDays(-1);
                var (priorBars, priorTicks, priorSuccess) = await _historicalDataLoader.LoadHistoricalDataAsync(simInstrument, historyEndDate);

                if (!priorSuccess)
                {
                    Logger.Error("[NiftyFuturesMetricsService] StartSimulationAsync: Prior historical data load failed");
                    return;
                }

                _historicalRangeATRBars = priorBars;
                _historicalTicks = priorTicks;

                LoggerFactory.Simulation.Info($"[NiftyFuturesMetricsService] Loaded {priorBars.Count} prior bars, {priorTicks.Count} ticks up to {historyEndDate:yyyy-MM-dd}");

                // Apply uptick/downtick rule
                _metricsCalculator.ApplyUptickDowntickRule(_historicalTicks);

                // Process prior historical VP to populate circular buffers (but NOT the simulation day)
                ProcessHistoricalVolumeProfileForPriorDays(simulationDate);

                // Now load the simulation date's data separately for progressive replay
                var (simDayBars, simDayTicks, simSuccess) = await _historicalDataLoader.LoadHistoricalDataAsync(simInstrument, simulationDate);

                if (!simSuccess)
                {
                    Logger.Warn("[NiftyFuturesMetricsService] StartSimulationAsync: Simulation day data load failed");
                    return;
                }

                // Filter to only simulation date bars and ticks
                var simDateBars = simDayBars.Where(b => b.Time.Date == simulationDate.Date).OrderBy(b => b.Time).ToList();
                var simDateTicks = simDayTicks.Where(t => t.Time.Date == simulationDate.Date).OrderBy(t => t.Time).ToList();

                LoggerFactory.Simulation.Info($"[NiftyFuturesMetricsService] Simulation date has {simDateBars.Count} bars, {simDateTicks.Count} ticks");

                if (simDateBars.Count == 0)
                {
                    Logger.Warn($"[NiftyFuturesMetricsService] No bars for simulation date {simulationDate:yyyy-MM-dd}");
                    return;
                }

                // Store simulation date bars/ticks for replay
                _historicalRangeATRBars.AddRange(simDateBars);
                _historicalTicks.AddRange(simDateTicks);

                // Apply uptick/downtick to new ticks
                _metricsCalculator.ApplyUptickDowntickRule(simDateTicks);

                // Initialize sim tick index to start of simulation day
                // History ticks contain everything now. We need index of first sim tick.
                _simTickIndex = _historicalRangeATRBars.Count - simDateBars.Count <= 0 ? 0 : 
                    _historicalTicks.Count - simDateTicks.Count; // Approximate start?
                
                // Safer: Find index of first tick >= simulationDate
                for(int i = Math.Max(0, _historicalTicks.Count - simDateTicks.Count - 1000); i < _historicalTicks.Count; i++)
                {
                    if (_historicalTicks[i].Time >= simulationDate.Date)
                    {
                        _simTickIndex = i;
                        break;
                    }
                }

                // Build replay bar list for simulation date
                _simReplayBars = simDateBars.Select(b => (b.Time, b.Close, b.Index)).ToList();
                _simReplayBarIndex = 0;

                // Reset session for simulation date
                _sessionStart = simDateBars.First().Time;
                _relMetrics.StartSession(_sessionStart);
                _compositeEngine.StartSession(simulationDate);
                _currentDayHigh = double.MinValue;
                _currentDayLow = double.MaxValue;
                _lastPrice = 0;

                _simDataReady = true;

                // Pre-compute all metrics for the simulation date
                PrecomputeSimulationMetrics(simulationDate);

                // Subscribe to simulation tick stream for progressive updates
                SubscribeToSimulationTicks();

                LoggerFactory.Simulation.Info($"[NiftyFuturesMetricsService] StartSimulationAsync complete - Replay ready with {_simReplayBars.Count} bars, {_simPrecomputedMetrics.Count} pre-computed metrics");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] StartSimulationAsync exception: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Process historical data for days BEFORE the simulation date to populate circular buffers.
        /// Does NOT process the simulation date itself - that's done progressively.
        /// </summary>
        private void ProcessHistoricalVolumeProfileForPriorDays(DateTime simulationDate)
        {
            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfileForPriorDays(): Processing prior to {simulationDate:yyyy-MM-dd}");

            if (_historicalRangeATRBars.Count == 0) return;

            var sessionGroups = _historicalRangeATRBars
                .Where(b => b.Time.Date < simulationDate.Date) // Exclude simulation date
                .GroupBy(b => b.Time.Date)
                .OrderBy(g => g.Key)
                .ToList();

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfileForPriorDays(): Found {sessionGroups.Count} prior sessions");

            int totalMinuteSamples = 0;
            
            // Shared search index
            int searchStartTickIndex = 0;

            // Build historical averages for RelMetrics from prior sessions
            foreach (var sessionGroup in sessionGroups)
            {
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();

                _vpEngine.Reset(VP_PRICE_INTERVAL);
                _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
                _sessionStart = sessionBars.First().Time;

                DateTime lastMinuteSampled = DateTime.MinValue;
                DateTime prevBarTime = DateTime.MinValue;

                foreach (var bar in sessionBars)
                {
                    // Use optimized tick processing
                    VolumeProfileLogic.ProcessTicksOptimized(
                        _historicalTicks,
                        ref searchStartTickIndex,
                        prevBarTime,
                        bar.Time,
                        bar.Close,
                        (price, volume, isBuy, tickTime) =>
                        {
                            _vpEngine.AddTick(price, volume, isBuy);
                            _rollingVpEngine.AddTick(price, volume, isBuy, tickTime);
                        });

                    _rollingVpEngine.ExpireOldData(bar.Time);
                    _vpEngine.SetClosePrice(bar.Close);
                    _rollingVpEngine.SetClosePrice(bar.Close);
                    var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                    var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                    if (vpMetrics.IsValid)
                    {
                        DateTime minuteBoundary = new DateTime(bar.Time.Year, bar.Time.Month, bar.Time.Day,
                            bar.Time.Hour, bar.Time.Minute, 0);

                        if (minuteBoundary > lastMinuteSampled)
                        {
                            _relMetrics.UpdateHistory(minuteBoundary, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                            if (rollingVpMetrics.IsValid)
                            {
                                _relMetrics.UpdateRollingHistory(minuteBoundary, rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                            }

                            lastMinuteSampled = minuteBoundary;
                            totalMinuteSamples++;
                        }
                    }
                    
                    prevBarTime = bar.Time;
                }
            }

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfileForPriorDays(): Built RelMetrics from {totalMinuteSamples} minute samples");

            // Build composite profiles from prior sessions (last 10 days before simulation)
            var recentSessions = sessionGroups
                .OrderByDescending(g => g.Key)
                .Take(10)
                .OrderBy(g => g.Key)
                .ToList();
                
            // Find start tick index for composite sessions
            int compositeSearchStart = 0;
            if (recentSessions.Count > 0)
            {
                DateTime firstCompositeTime = recentSessions.First().First().Time.Date;
                for(int i=0; i<_historicalTicks.Count; i++)
                {
                    if (_historicalTicks[i].Time >= firstCompositeTime)
                    {
                        compositeSearchStart = i;
                        break;
                    }
                }
            }

            foreach (var sessionGroup in recentSessions)
            {
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();
                DateTime sessionDate = sessionGroup.Key;

                _compositeEngine.StartSession(sessionDate);
                
                DateTime prevBarTime = DateTime.MinValue;

                foreach (var bar in sessionBars)
                {
                    VolumeProfileLogic.ProcessTicksOptimized(
                        _historicalTicks,
                        ref compositeSearchStart,
                        prevBarTime,
                        bar.Time,
                        0,
                        (price, volume, isBuy, tickTime) =>
                        {
                            _compositeEngine.AddTick(price, volume, isBuy, tickTime);
                        });
                    
                    prevBarTime = bar.Time;
                }

                _compositeEngine.FinalizeCurrentSession();
            }

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfileForPriorDays(): Composite profiles from {recentSessions.Count} sessions");

            // Reset VP engines for simulation day (will be populated progressively)
            _vpEngine.Reset(VP_PRICE_INTERVAL);
            _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
        }

        /// <summary>
        /// Subscribe to simulation tick stream to process bars progressively.
        /// </summary>
        private void SubscribeToSimulationTicks()
        {
            // Subscribe to the underlying price stream from SimulationService
            // When we receive a tick, check if any bars should close
            _simTickSubscription = MarketDataReactiveHub.Instance.OptionPriceBatchStream
                .Subscribe(batch =>
                {
                    if (!_simDataReady) return;

                    // Use the timestamp from any tick to advance simulation time
                    foreach (var update in batch)
                    {
                        ProcessSimulationTick(update.Timestamp);
                        break; // Just need one timestamp to advance
                    }
                });

            LoggerFactory.Simulation.Info("[NiftyFuturesMetricsService] Subscribed to simulation tick stream");
        }

        /// <summary>
        /// Pre-computes metrics for the entire simulation date.
        /// </summary>
        private void PrecomputeSimulationMetrics(DateTime simulationDate)
        {
            if (_simReplayBars == null || _simReplayBars.Count == 0) return;

            Logger.Info($"[NiftyFuturesMetricsService] Pre-computing {_simReplayBars.Count} metrics for {simulationDate:yyyy-MM-dd}");
            _simPrecomputedMetrics.Clear();

            // Save current state to restore after pre-computation
            var savedVpEngine = _vpEngine.Clone();
            var savedRollingVpEngine = _rollingVpEngine.Clone();
            var savedRelMetrics = _relMetrics.Clone();
            var savedComposite = _compositeEngine.Clone();
            double savedHigh = _currentDayHigh;
            double savedLow = _currentDayLow;
            double savedLastPrice = _lastPrice;
            DateTime savedLastBarTime = _lastBarCloseTime;
            int savedLastBarIndex = _lastBarIndex;
            int savedTickIndex = _simTickIndex;

            // We need to use a separate tick index for pre-computation to not mess up the live one
            int precomputeTickIndex = _simTickIndex;
            DateTime precomputeLastBarTime = _sessionStart;

            foreach (var bar in _simReplayBars)
            {
                // Process ticks for this bar
                DateTime prevBarTime = precomputeLastBarTime;
                
                VolumeProfileLogic.ProcessTicksOptimized(
                    _historicalTicks,
                    ref precomputeTickIndex,
                    prevBarTime,
                    bar.BarTime,
                    bar.ClosePrice,
                    (price, volume, isBuy, tTime) =>
                    {
                        _vpEngine.AddTick(price, volume, isBuy);
                        _rollingVpEngine.AddTick(price, volume, isBuy, tTime);
                        _compositeEngine.AddTick(price, volume, isBuy, tTime);

                        if (price > _currentDayHigh) _currentDayHigh = price;
                        if (price < _currentDayLow) _currentDayLow = price;
                        _lastPrice = price;
                    });

                _rollingVpEngine.ExpireOldData(bar.BarTime);
                _vpEngine.SetClosePrice(bar.ClosePrice);
                _rollingVpEngine.SetClosePrice(bar.ClosePrice);

                _lastBarCloseTime = bar.BarTime;
                _lastBarIndex = bar.BarIndex;

                // Recalculate metrics (this populates LatestMetrics)
                RecalculateAndPublishMetrics(true); // true to skip rel metrics update if we already did it or don't want to mess up buffers

                // Store a copy of the metrics
                _simPrecomputedMetrics.Add(LatestMetrics.Clone());

                precomputeLastBarTime = bar.BarTime;
            }

            // Restore state
            _vpEngine.Restore(savedVpEngine);
            _rollingVpEngine.Restore(savedRollingVpEngine);
            _relMetrics.Restore(savedRelMetrics);
            _compositeEngine.Restore(savedComposite);
            _currentDayHigh = savedHigh;
            _currentDayLow = savedLow;
            _lastPrice = savedLastPrice;
            _lastBarCloseTime = savedLastBarTime;
            _lastBarIndex = savedLastBarIndex;
            _simTickIndex = savedTickIndex;

            Logger.Info($"[NiftyFuturesMetricsService] Pre-computed {_simPrecomputedMetrics.Count} metrics");
        }

        /// <summary>
        /// Process simulation tick - advances through pre-built bars as simulation time progresses.
        /// </summary>
        private void ProcessSimulationTick(DateTime tickTime)
        {
            if (!_simDataReady || _simPrecomputedMetrics == null || _simPrecomputedMetrics.Count == 0)
                return;

            // Find the latest pre-computed metric that is applicable for the current tickTime
            NiftyFuturesVPMetrics applicableMetrics = null;
            
            // Optimization: since metrics are sorted by time, we can track an index or just find last
            foreach (var metrics in _simPrecomputedMetrics)
            {
                if (metrics.LastBarTime <= tickTime)
                {
                    applicableMetrics = metrics;
                }
                else
                {
                    break; // Future metric
                }
            }

            if (applicableMetrics != null && applicableMetrics != LatestMetrics)
            {
                LatestMetrics = applicableMetrics;
                _metricsSubject.OnNext(applicableMetrics);
                
                // Update internal tracking to stay in sync with what was published
                _lastBarCloseTime = applicableMetrics.LastBarTime;
                // Note: we don't need to update _vpEngine etc. because we are just replaying pre-computed metrics
            }
        }

        /// <summary>
        /// Stops simulation mode and restores live operation.
        /// </summary>
        public async Task StopSimulationAsync()
        {
            Logger.Info("[NiftyFuturesMetricsService] StopSimulationAsync(): Restoring live mode");
            LoggerFactory.Simulation.Info("[NiftyFuturesMetricsService] StopSimulationAsync - restoring live mode");

            // Stop simulation
            _simDataReady = false;
            _simTickSubscription?.Dispose();
            _simTickSubscription = null;

            // Reload current data and resume live updates
            await RequestHistoricalData();
            SubscribeToLiveTicks();
        }

        private async Task RequestHistoricalData()
        {
            // Request daily bars FIRST (for ADR, yearly, prior EOD) - these take time
            await RequestDailyBarsAsync();

            var (bars, ticks, success) = await _historicalDataLoader.LoadHistoricalDataAsync(_niftyIInstrument);

            if (!success)
            {
                Logger.Error("[NiftyFuturesMetricsService] RequestHistoricalData(): Data load failed");
                return;
            }

            _historicalRangeATRBars = bars;
            _historicalTicks = ticks;

            RangeBarLogger.Info($"[DATA_LOADED] Bars={_historicalRangeATRBars.Count}, Ticks={_historicalTicks.Count}");

            // Apply uptick/downtick rule
            _metricsCalculator.ApplyUptickDowntickRule(_historicalTicks);

            // Process historical VP
            ProcessHistoricalVolumeProfile();

            _isLivePhaseActive = true;

            await SetupLiveUpdateHandler();
        }

        private Task RequestDailyBarsAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                _dailyBarsRequest = new BarsRequest(_niftyIInstrument, YEARLY_BARS);
                _dailyBarsRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = BarsPeriodType.Minute,
                    Value = 1440
                };
                _dailyBarsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                _dailyBarsRequest.Update += OnDailyBarsUpdate;

                _dailyBarsRequest.Request((request, errorCode, errorMessage) =>
                {
                    if (errorCode == ErrorCode.NoError)
                    {
                        // Process all historical daily bars
                        var bars = request.Bars;
                        for (int i = 0; i < bars.Count; i++)
                        {
                            _compositeEngine.AddDailyBar(
                                bars.GetTime(i),
                                bars.GetOpen(i),
                                bars.GetHigh(i),
                                bars.GetLow(i),
                                bars.GetClose(i),
                                bars.GetVolume(i)
                            );
                        }
                        RangeBarLogger.Info($"[NiftyFuturesMetricsService] Daily bars loaded: {bars.Count} bars for ADR/yearly calculations");
                        RangeBarLogger.Info($"[DAILY_BARS] Loaded {bars.Count} bars for composite profiles");
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        Logger.Warn($"[NiftyFuturesMetricsService] Daily bars request failed: {errorCode} - {errorMessage}");
                        tcs.TrySetResult(false);
                    }
                });
            });

            return tcs.Task;
        }

        private void ProcessHistoricalVolumeProfile()
        {
            Logger.Info("[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Processing historical data...");

            if (_historicalRangeATRBars.Count == 0) return;

            var sessionGroups = _historicalRangeATRBars
                .GroupBy(b => b.Time.Date)
                .OrderBy(g => g.Key)
                .ToList();

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Found {sessionGroups.Count} sessions");
            RangeBarLogger.Info($"[HISTORICAL_SESSIONS] Processing {sessionGroups.Count} sessions, {_historicalRangeATRBars.Count} total bars");

            int totalMinuteSamples = 0;
            
            // Shared search index for sequential processing
            int searchStartTickIndex = 0;

            // First pass: Build historical averages for RelMetrics
            foreach (var sessionGroup in sessionGroups)
            {
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();

                _vpEngine.Reset(VP_PRICE_INTERVAL);
                _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
                _sessionStart = sessionBars.First().Time;

                DateTime lastMinuteSampled = DateTime.MinValue;
                DateTime prevBarTime = DateTime.MinValue;

                foreach (var bar in sessionBars)
                {
                    double lastPrice = bar.Close; // Fallback if no ticks
                    
                    // Use optimized tick processing
                    double updatedPrice = VolumeProfileLogic.ProcessTicksOptimized(
                        _historicalTicks,
                        ref searchStartTickIndex,
                        prevBarTime,
                        bar.Time,
                        _lastPrice, // Use class-level last price or tracked price? Re-using bar.Close/Open might be safer if gaps.
                        (price, volume, isBuy, tickTime) =>
                        {
                            _vpEngine.AddTick(price, volume, isBuy);
                            _rollingVpEngine.AddTick(price, volume, isBuy, tickTime);
                        });

                    _rollingVpEngine.ExpireOldData(bar.Time);

                    _vpEngine.SetClosePrice(bar.Close);
                    _rollingVpEngine.SetClosePrice(bar.Close);
                    var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                    var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                    if (vpMetrics.IsValid)
                    {
                        DateTime minuteBoundary = new DateTime(bar.Time.Year, bar.Time.Month, bar.Time.Day,
                            bar.Time.Hour, bar.Time.Minute, 0);

                        if (minuteBoundary > lastMinuteSampled)
                        {
                            _relMetrics.UpdateHistory(minuteBoundary, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                            if (rollingVpMetrics.IsValid)
                            {
                                _relMetrics.UpdateRollingHistory(minuteBoundary, rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                            }

                            lastMinuteSampled = minuteBoundary;
                            totalMinuteSamples++;
                        }
                    }
                    
                    prevBarTime = bar.Time;
                    _lastPrice = bar.Close;
                }
            }

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Built RelMetrics reference from {totalMinuteSamples} minute samples");

            // ═══════════════════════════════════════════════════════════════════
            // COMPOSITE PROFILE: Feed historical ticks to composite engine
            // Use sequential scanning for the last 10 sessions
            // ═══════════════════════════════════════════════════════════════════
            var recentSessions = sessionGroups
                .Where(g => g.Key.Date < DateTime.Today)  // Exclude today
                .OrderByDescending(g => g.Key)
                .Take(10)  // Last 10 completed trading days
                .OrderBy(g => g.Key)  // Process in chronological order
                .ToList();

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Building composite profiles from {recentSessions.Count} prior sessions");

            // Find start tick index for composite sessions efficiently
            int compositeSearchStart = 0;
            if (recentSessions.Count > 0)
            {
                DateTime firstCompositeTime = recentSessions.First().First().Time.Date; // Start of first day
                // Simple search to find the start index
                for(int i=0; i<_historicalTicks.Count; i++)
                {
                    if (_historicalTicks[i].Time >= firstCompositeTime)
                    {
                        compositeSearchStart = i;
                        break;
                    }
                }
            }

            foreach (var sessionGroup in recentSessions)
            {
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();
                DateTime sessionDate = sessionGroup.Key;

                // Start session in composite engine
                _compositeEngine.StartSession(sessionDate);

                int ticksProcessed = 0;
                DateTime prevBarTime = DateTime.MinValue;

                foreach (var bar in sessionBars)
                {
                    // Use optimized tick processing
                    VolumeProfileLogic.ProcessTicksOptimized(
                        _historicalTicks,
                        ref compositeSearchStart,
                        prevBarTime,
                        bar.Time,
                        0, // Price doesn't matter for counting ticks here, but we pass 0
                        (price, volume, isBuy, tickTime) =>
                        {
                            _compositeEngine.AddTick(price, volume, isBuy, tickTime);
                            ticksProcessed++;
                        });
                    
                    prevBarTime = bar.Time;
                }

                // Finalize the session (stores the profile for composite aggregation)
                _compositeEngine.FinalizeCurrentSession();
                Logger.Debug($"[NiftyFuturesMetricsService] Composite: Finalized session {sessionDate:yyyy-MM-dd} with {ticksProcessed} ticks");
            }

            RangeBarLogger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Composite profiles built, {_compositeEngine.DailyProfileCount} session profiles stored");

            // Start today's session in composite engine (will receive live ticks)
            _compositeEngine.StartSession(DateTime.Today);

            // Set _lastSessionDate for live session detection
            _lastSessionDate = sessionGroups.Last().Key;

            // Second pass: Process target session through RelMetrics to accumulate cumulative values
            var todaysBars = _historicalRangeATRBars.Where(b => b.Time.Date == DateTime.Today).ToList();
            List<RangeATRBar> targetSessionBars;
            
            if (todaysBars.Count > 0)
            {
                targetSessionBars = todaysBars;
                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from today's {todaysBars.Count} bars");
            }
            else
            {
                var lastSession = sessionGroups.Last();
                targetSessionBars = lastSession.ToList();
                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from last session {lastSession.Key:yyyy-MM-dd} ({lastSession.Count()} bars)");
            }

            // Re-process target session through VP engines AND RelMetrics
            _vpEngine.Reset(VP_PRICE_INTERVAL);
            _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
            _sessionStart = targetSessionBars.First().Time;
            _relMetrics.StartSession(_sessionStart);
            
            // Find start index for target session
            int targetSearchStart = 0;
            if (targetSessionBars.Count > 0)
            {
                // Can optimize: if target is "today", we might be able to start search from end of "composite" loop? 
                // But safer to just find it again or continue from where "First Pass" left off?
                // Actually "First Pass" went through EVERYTHING including today.
                // So First Pass `searchStartTickIndex` is at the end.
                // We need to rewind.
                DateTime targetStartTime = targetSessionBars.First().Time.AddMinutes(-10); // buffer
                 // Search for start index
                for(int i=0; i<_historicalTicks.Count; i++)
                {
                    if (_historicalTicks[i].Time >= targetSessionBars.First().Time.Date) // Start of day
                    {
                        targetSearchStart = i;
                        break;
                    }
                }
            }

            DateTime lastMinuteSampledForCumul = DateTime.MinValue;
            DateTime prevTargetBarTime = DateTime.MinValue;

            foreach (var bar in targetSessionBars)
            {
               VolumeProfileLogic.ProcessTicksOptimized(
                    _historicalTicks,
                    ref targetSearchStart,
                    prevTargetBarTime,
                    bar.Time,
                    _lastPrice,
                    (price, volume, isBuy, tickTime) =>
                    {
                        _vpEngine.AddTick(price, volume, isBuy);
                        _rollingVpEngine.AddTick(price, volume, isBuy, tickTime);

                        // Also feed to composite engine (for today's current session)
                        _compositeEngine.AddTick(price, volume, isBuy, tickTime);

                        // Track current day high/low
                        if (price > _currentDayHigh) _currentDayHigh = price;
                        if (price < _currentDayLow) _currentDayLow = price;
                        _lastPrice = price;
                    });

                _rollingVpEngine.ExpireOldData(bar.Time);

                _vpEngine.SetClosePrice(bar.Close);
                _rollingVpEngine.SetClosePrice(bar.Close);
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                if (vpMetrics.IsValid)
                {
                    DateTime minuteBoundary = new DateTime(bar.Time.Year, bar.Time.Month, bar.Time.Day,
                        bar.Time.Hour, bar.Time.Minute, 0);

                    if (minuteBoundary > lastMinuteSampledForCumul)
                    {
                        _relMetrics.Update(minuteBoundary, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                        if (rollingVpMetrics.IsValid)
                        {
                            _relMetrics.UpdateRolling(minuteBoundary, rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                        }

                        lastMinuteSampledForCumul = minuteBoundary;
                    }
                }
                
                prevTargetBarTime = bar.Time;
            }

            // Log cumulative tracking details
            var sessionTotals = _relMetrics.GetSessionTotals();
            Logger.Info($"[NiftyFuturesMetricsService] Historical VP Cumulative: Buy={sessionTotals.cumHVNBuy:F1}, Sell={sessionTotals.cumHVNSell:F1}");

            // Publish final metrics - skip RelMetrics update since we already processed the session
            RecalculateAndPublishMetrics(true);
        }

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

                // Note: Daily bars already loaded in RequestDailyBarsAsync() before historical data processing
            });
        }

        // Event Handlers for Live Data
        private void OnRangeATRBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            if (!_isLivePhaseActive) return;

            try
            {
                if (e.MaxIndex > _lastBarIndex)
                {
                    _lastBarIndex = e.MaxIndex;
                    _lastBarCloseTime = _rangeATRBarsRequest.Bars.GetTime(e.MaxIndex);
                    var bars = _rangeATRBarsRequest.Bars;

                    DateTime currentBarDate = _lastBarCloseTime.Date;
                    if (_lastSessionDate != DateTime.MinValue && currentBarDate > _lastSessionDate)
                    {
                        ResetSession(currentBarDate);
                    }
                    _lastSessionDate = currentBarDate;

                    ProcessPendingTicksIntoBar();

                    _vpEngine.SetClosePrice(bars.GetClose(e.MaxIndex));
                    _rollingVpEngine.SetClosePrice(bars.GetClose(e.MaxIndex)); // ensure rolling engine gets price update too
                    RecalculateAndPublishMetrics();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] OnRangeATRBarsUpdate(): Exception - {ex.Message}", ex);
            }
        }

        private void OnTickBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
             if (!_isLivePhaseActive) return;
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

        private void OnMinuteBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
             if (!_isLivePhaseActive) return;
             try
            {
                var bars = _minuteBarsRequest.Bars;
                if (bars == null || bars.Count == 0) return;

                DateTime minuteBarTime = bars.GetTime(e.MaxIndex);
                if (minuteBarTime <= _lastMinuteBarTime) return;
                _lastMinuteBarTime = minuteBarTime;

                _vpEngine.SetClosePrice(bars.GetClose(e.MaxIndex));
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                if (!vpMetrics.IsValid) return;

                _relMetrics.UpdateHistory(minuteBarTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);
                _relMetrics.Update(minuteBarTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] OnMinuteBarsUpdate Error: {ex.Message}");
            }
        }

        private void OnDailyBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            if (!_isLivePhaseActive) return;
            try
            {
                var bars = _dailyBarsRequest.Bars;
                if (bars == null || bars.Count == 0) return;

                // Process new daily bar
                for (int i = e.MinIndex; i <= e.MaxIndex; i++)
                {
                    _compositeEngine.AddDailyBar(
                        bars.GetTime(i),
                        bars.GetOpen(i),
                        bars.GetHigh(i),
                        bars.GetLow(i),
                        bars.GetClose(i),
                        bars.GetVolume(i)
                    );
                }

                Logger.Debug($"[NiftyFuturesMetricsService] OnDailyBarsUpdate: Processed bars {e.MinIndex}-{e.MaxIndex}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] OnDailyBarsUpdate Error: {ex.Message}");
            }
        }
        
        private void SubscribeToLiveTicks()
        {
             Logger.Info("[NiftyFuturesMetricsService] SubscribeToLiveTicks(): Subscribing to live tick stream...");
            _tickSubscription = MarketDataReactiveHub.Instance.NiftyFuturesStream
                .Where(update => update != null && update.Symbol != null)
                .Subscribe(OnLiveTickReceived);
        }

        private void OnLiveTickReceived(IndexPriceUpdate update)
        {
            if (!_isLivePhaseActive) return;
            _pendingTicks.Enqueue(new LiveTick
            {
                Time = DateTime.Now,
                Price = update.Price,
                Volume = 1,
                IsBuy = true // Will be fixed in ProcessPendingTicksIntoBar or by checking price diff if we tracked last price
            });
            // Update rolling VP with live ticks? The original code added them to PendingTicks and then ProcessPendingTicksIntoBar added them to VP engines.
            // So we just queue them here.
        }

        private void ProcessPendingTicksIntoBar()
        {
            var ticks = new List<LiveTick>();
            while (_pendingTicks.TryDequeue(out var liveTick)) ticks.Add(liveTick);
            if (ticks.Count == 0) return;

            DetermineBuySellDirection(ticks);

            foreach (var tick in ticks)
            {
                _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                _rollingVpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);

                // Feed ticks to composite engine (uses 5 rupee interval)
                _compositeEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);

                // Track current day high/low and last price
                if (tick.Price > _currentDayHigh) _currentDayHigh = tick.Price;
                if (tick.Price < _currentDayLow) _currentDayLow = tick.Price;
                _lastPrice = tick.Price;
            }
            _rollingVpEngine.ExpireOldData(DateTime.Now);
        }

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

        private void RecalculateAndPublishMetrics(bool skipRelMetricsUpdate = false)
        {
            try
            {
                var vpMetrics = _vpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                var rollingVpMetrics = _rollingVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                // Update RelMetrics with current VP state to compute relative/cumulative values
                if (vpMetrics.IsValid && !skipRelMetricsUpdate)
                {
                    DateTime barTime = _lastBarCloseTime != DateTime.MinValue ? _lastBarCloseTime : DateTime.Now;
                    _relMetrics.Update(barTime, vpMetrics.HVNBuyCount, vpMetrics.HVNSellCount, vpMetrics.ValueWidth);

                    if (rollingVpMetrics.IsValid)
                    {
                        _relMetrics.UpdateRolling(barTime,
                            rollingVpMetrics.HVNBuyCount, rollingVpMetrics.HVNSellCount, rollingVpMetrics.ValueWidth);
                    }
                }

                // Get relative/cumulative metrics from circular buffers
                double relHVNBuy = _relMetrics.RelHVNBuy.Count > 0 ? _relMetrics.RelHVNBuy[0] : 0;
                double relHVNSell = _relMetrics.RelHVNSell.Count > 0 ? _relMetrics.RelHVNSell[0] : 0;
                double relValueWidth = _relMetrics.RelValueWidth.Count > 0 ? _relMetrics.RelValueWidth[0] : 0;
                double cumHVNBuyRank = _relMetrics.CumHVNBuyRank.Count > 0 ? _relMetrics.CumHVNBuyRank[0] : 100;
                double cumHVNSellRank = _relMetrics.CumHVNSellRank.Count > 0 ? _relMetrics.CumHVNSellRank[0] : 100;
                double cumValueWidthRank = _relMetrics.CumValueWidthRank.Count > 0 ? _relMetrics.CumValueWidthRank[0] : 100;

                double relHVNBuyRolling = _relMetrics.RelHVNBuyRolling.Count > 0 ? _relMetrics.RelHVNBuyRolling[0] : 0;
                double relHVNSellRolling = _relMetrics.RelHVNSellRolling.Count > 0 ? _relMetrics.RelHVNSellRolling[0] : 0;
                double relValueWidthRolling = _relMetrics.RelValueWidthRolling.Count > 0 ? _relMetrics.RelValueWidthRolling[0] : 0;
                double cumHVNBuyRollingRank = _relMetrics.CumHVNBuyRollingRank.Count > 0 ? _relMetrics.CumHVNBuyRollingRank[0] : 100;
                double cumHVNSellRollingRank = _relMetrics.CumHVNSellRollingRank.Count > 0 ? _relMetrics.CumHVNSellRollingRank[0] : 100;
                double cumValueWidthRollingRank = _relMetrics.CumValueWidthRollingRank.Count > 0 ? _relMetrics.CumValueWidthRollingRank[0] : 100;

                // Calculate composite profile metrics
                double currentPrice = _lastPrice > 0 ? _lastPrice : vpMetrics.POC;
                double dayHigh = _currentDayHigh > double.MinValue ? _currentDayHigh : vpMetrics.VAH;
                double dayLow = _currentDayLow < double.MaxValue ? _currentDayLow : vpMetrics.VAL;
                var compositeMetrics = _compositeEngine.Recalculate(currentPrice, dayHigh, dayLow);

                var result = new NiftyFuturesVPMetrics
                {
                    IsValid = vpMetrics.IsValid,
                    POC = vpMetrics.POC,
                    VAH = vpMetrics.VAH,
                    VAL = vpMetrics.VAL,
                    VWAP = vpMetrics.VWAP,
                    HVNs = vpMetrics.HVNs,
                    HVNBuyCount = vpMetrics.HVNBuyCount,
                    HVNSellCount = vpMetrics.HVNSellCount,
                    HVNBuyVolume = vpMetrics.HVNBuyVolume,
                    HVNSellVolume = vpMetrics.HVNSellVolume,

                    RelHVNBuy = relHVNBuy,
                    RelHVNSell = relHVNSell,
                    RelValueWidth = relValueWidth,
                    CumHVNBuyRank = cumHVNBuyRank,
                    CumHVNSellRank = cumHVNSellRank,
                    CumValueWidthRank = cumValueWidthRank,

                    RollingPOC = rollingVpMetrics.IsValid ? rollingVpMetrics.POC : 0,
                    RollingVAH = rollingVpMetrics.IsValid ? rollingVpMetrics.VAH : 0,
                    RollingVAL = rollingVpMetrics.IsValid ? rollingVpMetrics.VAL : 0,
                    RollingHVNBuyCount = rollingVpMetrics.IsValid ? rollingVpMetrics.HVNBuyCount : 0,
                    RollingHVNSellCount = rollingVpMetrics.IsValid ? rollingVpMetrics.HVNSellCount : 0,

                    RelHVNBuyRolling = relHVNBuyRolling,
                    RelHVNSellRolling = relHVNSellRolling,
                    RelValueWidthRolling = relValueWidthRolling,
                    CumHVNBuyRollingRank = cumHVNBuyRollingRank,
                    CumHVNSellRollingRank = cumHVNSellRollingRank,
                    CumValueWidthRollingRank = cumValueWidthRollingRank,

                    // Composite profile metrics
                    Composite = compositeMetrics,

                    BarCount = _historicalRangeATRBars.Count(b => b.Time >= _sessionStart),
                    Symbol = _niftyFuturesSymbol,
                    LastUpdate = DateTime.Now,
                    LastBarTime = _lastBarCloseTime
                };

                LatestMetrics = result;
                _metricsSubject.OnNext(result);

                // Log metrics
                double valueWidth = result.VAH - result.VAL;
                double rollingValueWidth = result.RollingVAH - result.RollingVAL;
                Logger.Info($"[NiftyFuturesMetricsService] Session: POC={result.POC:F2}, ValW={valueWidth:F0}, HVNBuy={result.HVNBuyCount}, HVNSell={result.HVNSellCount} | " +
                    $"Rel: B={relHVNBuy:F0} S={relHVNSell:F0} W={relValueWidth:F0} | Cum: B={cumHVNBuyRank:F0} S={cumHVNSellRank:F0} W={cumValueWidthRank:F0}");
                RangeBarLogger.LogVPMetrics(_niftyFuturesSymbol, result.POC, result.VAH, result.VAL, result.VWAP, result.HVNs?.Count ?? 0, result.BarCount);
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyFuturesMetricsService] RecalculateAndPublishMetrics(): Exception - {ex.Message}", ex);
            }
        }

        public void ResetSession(DateTime? date = null)
        {
             _vpEngine.Reset(VP_PRICE_INTERVAL);
             _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
             _relMetrics.StartSession(date ?? DateTime.Now);
             _compositeEngine.StartSession(date ?? DateTime.Now);
             while (_pendingTicks.TryDequeue(out _)) { }
             _sessionStart = date ?? DateTime.Now;

             // Reset day high/low tracking
             _currentDayHigh = double.MinValue;
             _currentDayLow = double.MaxValue;
             _lastPrice = 0;

             RecalculateAndPublishMetrics();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            Stop();
            _metricsSubject?.Dispose();
            _disposables?.Dispose();
        }
    }
}
