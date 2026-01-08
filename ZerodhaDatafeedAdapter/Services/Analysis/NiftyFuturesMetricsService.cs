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
        private Instrument _niftyFuturesInstrument;
        private Instrument _niftyIInstrument;
        private string _niftyFuturesSymbol;
        private double _tickSize = 0.05;

        // Components
        private readonly InternalVolumeProfileEngine _vpEngine = new InternalVolumeProfileEngine();
        private readonly RollingVolumeProfileEngine _rollingVpEngine = new RollingVolumeProfileEngine(60);
        private readonly VPRelativeMetricsEngine _relMetrics = new VPRelativeMetricsEngine();
        private InternalTickToBarMapper _tickMapper;

        private DateTime _lastMinuteBarTime = DateTime.MinValue;

        // Data
        private List<RangeATRBar> _historicalRangeATRBars = new List<RangeATRBar>();
        private List<HistoricalTick> _historicalTicks = new List<HistoricalTick>();
        private readonly ConcurrentQueue<LiveTick> _pendingTicks = new ConcurrentQueue<LiveTick>();

        private DateTime _lastBarCloseTime = DateTime.MinValue;
        private int _lastBarIndex = -1;
        private DateTime _sessionStart = DateTime.MinValue;
        private DateTime _lastSessionDate = DateTime.MinValue;
        private bool _isHistoricalPhaseComplete = false; // Could potentially remove if state is managed better
        private bool _isLivePhaseActive = false;

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
            _tickSubscription?.Dispose();

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
            });
            Logger.Info("[NiftyFuturesMetricsService] Stop(): Service stopped");
        }

        private async Task RequestHistoricalData()
        {
            var (bars, ticks, success) = await _historicalDataLoader.LoadHistoricalDataAsync(_niftyIInstrument); // Or _RangeATRBarsRequest? No, it uses internal requests.

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

            // Build tick map
            _tickMapper = _metricsCalculator.BuildTickToBarMapping(_historicalRangeATRBars, _historicalTicks);

            // Process historical VP
            ProcessHistoricalVolumeProfile();

            _isHistoricalPhaseComplete = true;
            _isLivePhaseActive = true;

            await SetupLiveUpdateHandler();
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

            // First pass: Build historical averages for RelMetrics
            foreach (var sessionGroup in sessionGroups)
            {
                var sessionBars = sessionGroup.OrderBy(b => b.Time).ToList();

                _vpEngine.Reset(VP_PRICE_INTERVAL);
                _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
                _sessionStart = sessionBars.First().Time;

                DateTime lastMinuteSampled = DateTime.MinValue;

                foreach (var bar in sessionBars)
                {
                    var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);

                    if (ticksForBar.Count > 0)
                    {
                        foreach (var tick in ticksForBar)
                        {
                            _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                            _rollingVpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);
                        }
                    }

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
                }
            }

            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Built RelMetrics reference from {totalMinuteSamples} minute samples");

            // Set _lastSessionDate for live session detection
            _lastSessionDate = sessionGroups.Last().Key;

            // Second pass: Process target session through RelMetrics to accumulate cumulative values
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
                var lastSession = sessionGroups.Last();
                targetSessionBars = lastSession.ToList();
                targetSessionDate = lastSession.Key;
                Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Final state from last session {lastSession.Key:yyyy-MM-dd} ({lastSession.Count()} bars)");
            }

            // Re-process target session through VP engines AND RelMetrics
            _vpEngine.Reset(VP_PRICE_INTERVAL);
            _rollingVpEngine.Reset(VP_PRICE_INTERVAL);
            _sessionStart = targetSessionBars.First().Time;
            _relMetrics.StartSession(_sessionStart);

            DateTime lastMinuteSampledForCumul = DateTime.MinValue;

            foreach (var bar in targetSessionBars)
            {
                var ticksForBar = _tickMapper.GetTicksForBar(bar.Index);
                foreach (var tick in ticksForBar)
                {
                    _vpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy);
                    _rollingVpEngine.AddTick(tick.Price, tick.Volume, tick.IsBuy, tick.Time);
                }

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
            }

            // Log cumulative tracking details
            var sessionTotals = _relMetrics.GetSessionTotals();
            Logger.Info($"[NiftyFuturesMetricsService] ProcessHistoricalVolumeProfile(): Cumulative tracking - " +
                $"CumHVNBuy={sessionTotals.cumHVNBuy:F1}/Ref={sessionTotals.refHVNBuy:F1}, " +
                $"CumHVNSell={sessionTotals.cumHVNSell:F1}/Ref={sessionTotals.refHVNSell:F1}, " +
                $"CumValW={sessionTotals.cumValWidth:F1}/Ref={sessionTotals.refValWidth:F1}");

            // Publish final metrics - skip RelMetrics update since we already processed the session
            RecalculateAndPublishMetrics(skipRelMetricsUpdate: true);
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
             // ... logic from original file ...
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
            }
            _rollingVpEngine.ExpireOldData(DateTime.Now); // Or bar time
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
             while (_pendingTicks.TryDequeue(out _)) { }
             _sessionStart = date ?? DateTime.Now;
             
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
