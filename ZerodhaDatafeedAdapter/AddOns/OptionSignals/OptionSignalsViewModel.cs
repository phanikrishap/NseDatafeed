using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;
using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals
{
    /// <summary>
    /// Tracks VP state for a single option symbol (CE or PE at a strike)
    /// VP is computed at RangeATR bar close using ticks from that bar's time range.
    /// </summary>
    internal class OptionVPState
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
            Subscriptions?.Dispose();
            TickDataReady?.Dispose();
            RangeBarsReady?.Dispose();
            RangeBarUpdates?.Dispose();
            RangeBarsRequest?.Dispose();
            TickBarsRequest?.Dispose();
        }
    }

    public class OptionSignalsViewModel : ViewModelBase, IDisposable
    {
        private CompositeDisposable _subscriptions;
        private readonly Dictionary<string, OptionVPState> _vpStates = new Dictionary<string, OptionVPState>();
        private readonly Dictionary<string, (OptionSignalsRow row, string type)> _symbolToRowMap = new Dictionary<string, (OptionSignalsRow, string)>();
        private readonly Subject<OptionsGeneratedEvent> _strikeGenerationTrigger = new Subject<OptionsGeneratedEvent>();
        private readonly Dispatcher _dispatcher;
        private readonly object _rowsLock = new object();

        // Dedicated logger
        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        // VP Configuration
        private const double VALUE_AREA_PERCENT = 0.70;
        private const double HVN_RATIO = 0.25;

        // Data Collections
        public ObservableCollection<OptionSignalsRow> Rows { get; } = new ObservableCollection<OptionSignalsRow>();

        // Properties
        private string _underlying = "NIFTY";
        private string _expiry = "---";
        private string _statusText = "Waiting for Data...";
        private bool _isBusy;
        private DateTime? _selectedExpiry;

        public string Underlying
        {
            get => _underlying;
            set { if (_underlying != value) { _underlying = value; OnPropertyChanged(); } }
        }

        public string Expiry
        {
            get => _expiry;
            set { if (_expiry != value) { _expiry = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        public new bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        public OptionSignalsViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            BindingOperations.EnableCollectionSynchronization(Rows, _rowsLock);
            _log.Info("[OptionSignalsViewModel] Initialized");
            SetupInternalPipelines();
        }

        private void SetupInternalPipelines()
        {
            _subscriptions = new CompositeDisposable();

            var strikePipeline = _strikeGenerationTrigger
                .Select(evt => Observable.FromAsync(ct => SyncAndPopulateStrikes(evt, ct)))
                .Switch()
                .Subscribe(
                    _ => _log.Debug("[OptionSignalsViewModel] Strike generation pipeline iteration completed"),
                    ex => _log.Error($"[OptionSignalsViewModel] Strike pipeline error: {ex.Message}")
                );

            _subscriptions.Add(strikePipeline);
        }

        public void StartServices()
        {
            _log.Info("[OptionSignalsViewModel] Starting services");
            var hub = MarketDataReactiveHub.Instance;

            _subscriptions.Add(hub.ProjectedOpenStream
                .Where(s => s.IsComplete)
                .Take(1)
                .ObserveOnDispatcher()
                .Subscribe(OnProjectedOpenReady));

            _subscriptions.Add(hub.OptionsGeneratedStream
                .Subscribe(evt => _strikeGenerationTrigger.OnNext(evt)));

            _subscriptions.Add(hub.OptionPriceBatchStream
                .ObserveOnDispatcher()
                .Subscribe(OnOptionPriceBatch));
        }

        public void StopServices()
        {
            _log.Info("[OptionSignalsViewModel] Stopping services");
            _subscriptions?.Clear();
            ClearAllVPStates();
        }

        private void OnProjectedOpenReady(ProjectedOpenState state)
        {
            _log.Info($"[OptionSignalsViewModel] Projected Open Ready: {state.NiftyProjectedOpen:F2}");
        }

        private async Task SyncAndPopulateStrikes(OptionsGeneratedEvent evt, System.Threading.CancellationToken ct)
        {
            if (evt?.Options == null || evt.Options.Count == 0) return;
            _log.Info($"[OptionSignalsViewModel] SyncAndPopulateStrikes started for {evt.SelectedUnderlying} ATM={evt.ATMStrike}");

            await _dispatcher.InvokeAsync(() => {
                Underlying = evt.SelectedUnderlying;
                _selectedExpiry = evt.SelectedExpiry;
                Expiry = evt.SelectedExpiry.ToString("dd-MMM-yyyy");
                IsBusy = true;
                StatusText = "Synchronizing Historical Tick Data...";
            });

            int atmStrike = (int)evt.ATMStrike;
            int step = 50;
            var uniqueStrikes = evt.Options.Where(o => o.strike.HasValue)
                .Select(o => (int)o.strike.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (uniqueStrikes.Count >= 2) step = uniqueStrikes[1] - uniqueStrikes[0];

            var strikesToLoad = new List<int>();
            for (int i = -10; i <= 10; i++) strikesToLoad.Add(atmStrike + (i * step));

            var symbolsToSync = new List<string>();
            var strikeToSymbolMap = new Dictionary<(int strike, string type), string>();

            foreach (var strike in strikesToLoad)
            {
                var ce = evt.Options.FirstOrDefault(o => o.strike == strike && o.option_type == "CE");
                var pe = evt.Options.FirstOrDefault(o => o.strike == strike && o.option_type == "PE");
                if (ce != null) { symbolsToSync.Add(ce.symbol); strikeToSymbolMap[(strike, "CE")] = ce.symbol; }
                if (pe != null) { symbolsToSync.Add(pe.symbol); strikeToSymbolMap[(strike, "PE")] = pe.symbol; }
            }

            if (symbolsToSync.Count == 0)
            {
                await _dispatcher.InvokeAsync(() => {
                    StatusText = "No symbols found for requested strikes.";
                    IsBusy = false;
                });
                return;
            }

            // OPTIMIZATION: Reduced timeout and parallel sync with fast-fail
            var coordinator = HistoricalTickDataCoordinator.Instance;
            const int SYNC_TIMEOUT_SECONDS = 15; // Reduced from 60s

            var syncTaskList = symbolsToSync.Select(async sym =>
            {
                try {
                    await coordinator.GetInstrumentTickStatusStream(sym)
                        .Where(s => s.State == TickDataState.Ready || s.State == TickDataState.Failed || s.State == TickDataState.NoData)
                        .Take(1)
                        .Timeout(TimeSpan.FromSeconds(SYNC_TIMEOUT_SECONDS))
                        .ToTask(ct);
                } catch (OperationCanceledException) {
                    throw;
                } catch (TimeoutException) {
                    // Don't log individual timeouts - just continue
                } catch (Exception ex) {
                    _log.Warn($"[OptionSignalsViewModel] Sync error for {sym}: {ex.Message}");
                }
            }).ToList();

            try
            {
                await Task.WhenAll(syncTaskList);
                _log.Info($"[OptionSignalsViewModel] Historical sync finished for {evt.SelectedUnderlying}");
            }
            catch (OperationCanceledException)
            {
                _log.Debug($"[OptionSignalsViewModel] SyncAndPopulateStrikes cancelled");
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"[OptionSignalsViewModel] Sync wait failed: {ex.Message}");
            }

            await _dispatcher.InvokeAsync(() =>
            {
                lock (_rowsLock)
                {
                    Rows.Clear();
                    _symbolToRowMap.Clear();
                    ClearAllVPStates();

                    foreach (var strike in strikesToLoad)
                    {
                        var row = new OptionSignalsRow { Strike = strike, IsATM = (strike == (int)evt.ATMStrike) };

                        if (strikeToSymbolMap.TryGetValue((strike, "CE"), out var ceSym))
                        {
                            row.CESymbol = ceSym;
                            _symbolToRowMap[ceSym] = (row, "CE");
                            CreateVPState(ceSym, strike, "CE", row);
                        }

                        if (strikeToSymbolMap.TryGetValue((strike, "PE"), out var peSym))
                        {
                            row.PESymbol = peSym;
                            _symbolToRowMap[peSym] = (row, "PE");
                            CreateVPState(peSym, strike, "PE", row);
                        }

                        Rows.Add(row);
                    }

                    StatusText = $"Monitoring {Rows.Count} strikes. (Historical Sync OK)";
                    IsBusy = false;
                }
            });
        }

        private void CreateVPState(string symbol, int strike, string type, OptionSignalsRow row)
        {
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null)
            {
                _log.Warn($"[OptionSignalsViewModel] Instrument not found for {symbol}");
                return;
            }

            var state = new OptionVPState
            {
                Symbol = symbol,
                Type = type,
                Row = row
            };

            // Setup Rx pipeline: wait for both tick and range data, then process
            var bothReadyPipeline = Observable.CombineLatest(
                state.TickDataReady.Where(r => r),
                state.RangeBarsReady.Where(r => r),
                (tickReady, rangeReady) => true
            )
            .Take(1)
            .Subscribe(_ =>
            {
                _log.Debug($"[VP] {symbol} both tick and range data ready, processing historical");
                ProcessHistoricalData(state);
            });

            state.Subscriptions.Add(bothReadyPipeline);

            // Setup real-time bar update pipeline (only processes after initial data is ready)
            var realTimeUpdatePipeline = state.RangeBarUpdates
                .Where(_ => state.LastRangeBarIndex >= 0) // Only after initial processing
                .Subscribe(e => OnRealTimeBarUpdate(state, e));

            state.Subscriptions.Add(realTimeUpdatePipeline);

            NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                // Create Tick BarsRequest for Volume Profile (request first)
                var tickRequest = new BarsRequest(instrument, 10000);
                tickRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = BarsPeriodType.Tick,
                    Value = 1
                };
                tickRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                tickRequest.Request((r, code, msg) => {
                    if (code == ErrorCode.NoError)
                    {
                        _log.Debug($"[OptionSignalsViewModel] TickBars Success: {symbol}, count={r.Bars.Count}");
                        state.TickDataReady.OnNext(true);
                    }
                    else
                    {
                        _log.Warn($"[OptionSignalsViewModel] TickBars Failed: {symbol} - {msg}");
                        state.TickDataReady.OnNext(true); // Signal anyway so we don't block
                    }
                });

                state.TickBarsRequest = tickRequest;

                // Create RangeATR BarsRequest
                var rangeRequest = new BarsRequest(instrument, 100);
                rangeRequest.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = (BarsPeriodType)7015, // RangeATR
                    Value = 1,
                    Value2 = 3,
                    BaseBarsPeriodValue = 1
                };
                rangeRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                rangeRequest.Update += (s, e) => state.RangeBarUpdates.OnNext(e);

                rangeRequest.Request((r, code, msg) => {
                    if (code == ErrorCode.NoError)
                    {
                        _log.Debug($"[OptionSignalsViewModel] RangeBars Success: {symbol}, count={r.Bars.Count}");
                        state.RangeBarsReady.OnNext(true);
                    }
                    else
                    {
                        _log.Warn($"[OptionSignalsViewModel] RangeBars Failed: {symbol} - {msg}");
                        state.RangeBarsReady.OnNext(true); // Signal anyway
                    }
                });

                state.RangeBarsRequest = rangeRequest;
            });

            _vpStates[symbol] = state;
        }

        /// <summary>
        /// Process all historical data once both tick and range bars are ready.
        /// Context-aware: Uses prior working day data if pre-market, today's data otherwise.
        /// </summary>
        private void ProcessHistoricalData(OptionVPState state)
        {
            var rangeBars = state.RangeBarsRequest?.Bars;
            var tickBars = state.TickBarsRequest?.Bars;

            if (rangeBars == null || rangeBars.Count == 0)
            {
                _log.Warn($"[VP] {state.Symbol} no range bars available");
                return;
            }

            if (tickBars == null || tickBars.Count == 0)
            {
                _log.Warn($"[VP] {state.Symbol} no tick bars available, updating ATR only");
                // Still update ATR display
                int lastBarIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastBarIdx), rangeBars.GetTime(lastBarIdx));
                state.LastRangeBarIndex = lastBarIdx;
                state.LastBarCloseTime = rangeBars.GetTime(lastBarIdx);
                return;
            }

            // Determine target date based on market phase:
            // - PreMarket (before 9:15 on trading day): use prior working day
            // - MarketHours/PostMarket: use today
            // - Non-trading day (weekend/holiday): use prior working day
            DateTime targetDate = GetTargetDataDate();
            _log.Debug($"[VP] {state.Symbol} target date for VP: {targetDate:yyyy-MM-dd} (IsPreMarket={DateTimeHelper.IsPreMarket()}, IsTradingDay={!DateTimeHelper.IsNonTradingDay(DateTime.Today)})");

            // Build sorted tick time index for target date
            var tickTimes = new List<(DateTime time, int index)>(tickBars.Count);
            for (int i = 0; i < tickBars.Count; i++)
            {
                var t = tickBars.GetTime(i);
                if (t.Date == targetDate)
                    tickTimes.Add((t, i));
            }

            if (tickTimes.Count == 0)
            {
                _log.Warn($"[VP] {state.Symbol} no ticks for {targetDate:yyyy-MM-dd}");
                int lastBarIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastBarIdx), rangeBars.GetTime(lastBarIdx));
                state.LastRangeBarIndex = lastBarIdx;
                state.LastBarCloseTime = rangeBars.GetTime(lastBarIdx);
                return;
            }

            DateTime prevBarTime = DateTime.MinValue;
            int barsProcessed = 0;
            double lastPrice = 0;
            int tickSearchStart = 0;

            // Process each historical bar with its ticks
            for (int barIdx = 0; barIdx < rangeBars.Count; barIdx++)
            {
                DateTime barTime = rangeBars.GetTime(barIdx);

                // Skip bars not from target date
                if (barTime.Date != targetDate)
                {
                    prevBarTime = barTime;
                    continue;
                }

                double closePrice = rangeBars.GetClose(barIdx);

                // Use pre-built index and track position for O(n) total instead of O(n²)
                lastPrice = ProcessTicksOptimized(state, tickBars, tickTimes, ref tickSearchStart, prevBarTime, barTime, lastPrice);

                // Update VP close price and expire old rolling data
                state.SessionVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.ExpireOldData(barTime);

                // Update LastClosePrice BEFORE RecalculateVP so Price Momentum and VWAP Score use current price
                state.LastClosePrice = closePrice;

                // Recalculate VP and check for trend changes on every bar (accuracy is critical)
                RecalculateVP(state, barTime);

                prevBarTime = barTime;
                barsProcessed++;
            }

            state.LastBarCloseTime = prevBarTime;
            state.LastRangeBarIndex = rangeBars.Count - 1;
            state.LastClosePrice = lastPrice;

            // Update ATR display with last bar
            if (rangeBars.Count > 0)
            {
                int lastIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastIdx), rangeBars.GetTime(lastIdx));
            }

            _log.Info($"[VP] {state.Symbol} historical processing complete: {barsProcessed} bars");
        }

        /// <summary>
        /// Optimized tick processing using pre-built index and tracking search position.
        /// Feeds ticks to VP engines and CD Momentum engine.
        /// </summary>
        private double ProcessTicksOptimized(OptionVPState state, Bars tickBars,
            List<(DateTime time, int index)> tickTimes, ref int searchStart,
            DateTime prevBarTime, DateTime currentBarTime, double lastPrice)
        {
            int tickCount = 0;

            // Start new bar for CD momentum engine
            state.CDMomoEngine.StartNewBar();

            // Scan from last position (O(n) total across all bars instead of O(n²))
            while (searchStart < tickTimes.Count)
            {
                var (tickTime, tickIdx) = tickTimes[searchStart];

                // Skip ticks before or at previous bar close
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                {
                    searchStart++;
                    continue;
                }

                // Stop if tick is after current bar close time
                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(tickIdx);
                long volume = tickBars.GetVolume(tickIdx);
                bool isBuy = price >= lastPrice;

                // Feed to VP engines
                state.SessionVPEngine.AddTick(price, volume, isBuy);
                state.RollingVPEngine.AddTick(price, volume, isBuy, tickTime);

                // Feed to CD Momentum engine
                state.CDMomoEngine.AddTick(price, volume, isBuy, tickTime);

                lastPrice = price;
                tickCount++;
                searchStart++;
            }

            state.LastVPTickIndex = searchStart > 0 ? tickTimes[searchStart - 1].index : -1;
            return lastPrice;
        }

        /// <summary>
        /// Process ticks within a time window (between prevBarTime and currentBarTime).
        /// Returns the last processed price.
        /// Feeds ticks to VP engines and CD Momentum engine.
        /// </summary>
        private double ProcessTicksInWindow(OptionVPState state, Bars tickBars, DateTime prevBarTime, DateTime currentBarTime, double lastPrice)
        {
            DateTime today = DateTime.Today;
            int tickCount = 0;
            int startIdx = state.LastVPTickIndex + 1;
            if (startIdx < 0) startIdx = 0;

            // Start new bar for CD momentum engine
            state.CDMomoEngine.StartNewBar();

            for (int i = startIdx; i < tickBars.Count; i++)
            {
                var tickTime = tickBars.GetTime(i);

                // Skip ticks before today
                if (tickTime.Date < today) continue;

                // Skip ticks before or at previous bar close (already processed)
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime) continue;

                // Stop if tick is after current bar close time
                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(i);
                long volume = tickBars.GetVolume(i);
                bool isBuy = price >= lastPrice;

                // Add to both VP engines
                state.SessionVPEngine.AddTick(price, volume, isBuy);
                state.RollingVPEngine.AddTick(price, volume, isBuy, tickTime);

                // Feed to CD Momentum engine
                state.CDMomoEngine.AddTick(price, volume, isBuy, tickTime);

                lastPrice = price;
                tickCount++;
                state.LastVPTickIndex = i;
            }

            if (tickCount > 0)
            {
                _log.Debug($"[VP] {state.Symbol} processed {tickCount} ticks for bar at {currentBarTime:HH:mm:ss}");
            }

            return lastPrice;
        }

        /// <summary>
        /// Handle real-time bar updates (after initial historical processing).
        /// </summary>
        private void OnRealTimeBarUpdate(OptionVPState state, BarsUpdateEventArgs e)
        {
            var bars = state.RangeBarsRequest?.Bars;
            if (bars == null) return;

            int closedBarIndex = e.MaxIndex - 1;
            if (closedBarIndex < 0 || closedBarIndex <= state.LastRangeBarIndex) return;

            double close = bars.GetClose(closedBarIndex);
            DateTime barTime = bars.GetTime(closedBarIndex);

            // Update ATR display
            UpdateAtrDisplay(state, close, barTime);

            // Process ticks from previous bar close to current bar close
            var tickBars = state.TickBarsRequest?.Bars;
            if (tickBars != null && tickBars.Count > 0)
            {
                ProcessTicksInWindow(state, tickBars, state.LastBarCloseTime, barTime, state.LastClosePrice);
            }

            // Update VP and recalculate
            state.SessionVPEngine.SetClosePrice(close);
            state.RollingVPEngine.SetClosePrice(close);
            state.RollingVPEngine.ExpireOldData(barTime);

            // Update LastClosePrice with range bar close (used for Price Momentum and VWAP Score)
            state.LastClosePrice = close;

            RecalculateVP(state, barTime);

            state.LastBarCloseTime = barTime;
            state.LastRangeBarIndex = closedBarIndex;
        }

        private void UpdateAtrDisplay(OptionVPState state, double close, DateTime barTime)
        {
            string timeStr = barTime.ToString("HH:mm:ss");
            if (state.Type == "CE")
            {
                state.Row.CEAtrLTP = close.ToString("F2");
                state.Row.CEAtrTime = timeStr;
            }
            else
            {
                state.Row.PEAtrLTP = close.ToString("F2");
                state.Row.PEAtrTime = timeStr;
            }
        }

        private void RecalculateVP(OptionVPState state, DateTime currentTime)
        {
            // Calculate Session VP
            var sessResult = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

            // Calculate Rolling VP
            var rollResult = state.RollingVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

            // Close CD Momentum bar and get result (smaMomentum style: Momentum + Smooth)
            var cdMomoResult = state.CDMomoEngine.CloseBar(currentTime);

            // Process Price Momentum (using last close price)
            var priceMomoResult = state.PriceMomoEngine.ProcessBar(state.LastClosePrice, currentTime);

            // Determine HVN trends
            HvnTrend sessTrend = DetermineTrend(sessResult);
            HvnTrend rollTrend = DetermineTrend(rollResult);

            // Track HVN trend onset times
            string sessTrendTime = state.Type == "CE" ? state.Row.CETrendSessTime : state.Row.PETrendSessTime;
            string rollTrendTime = state.Type == "CE" ? state.Row.CETrendRollTime : state.Row.PETrendRollTime;

            // Update session trend time if changed
            if (sessTrend != state.LastSessionTrend && sessTrend != HvnTrend.Neutral)
            {
                state.SessionTrendOnsetTime = currentTime;
                sessTrendTime = currentTime.ToString("HH:mm:ss");
                _log.Info($"[Trend] {state.Symbol} Session trend changed to {sessTrend} at {sessTrendTime}");
            }
            state.LastSessionTrend = sessTrend;

            // Update rolling trend time if changed
            if (rollTrend != state.LastRollingTrend && rollTrend != HvnTrend.Neutral)
            {
                state.RollingTrendOnsetTime = currentTime;
                rollTrendTime = currentTime.ToString("HH:mm:ss");
                _log.Info($"[Trend] {state.Symbol} Rolling trend changed to {rollTrend} at {rollTrendTime}");
            }
            state.LastRollingTrend = rollTrend;

            // Calculate VWAP scores based on current price position relative to SD bands
            int sessVwapScore = VWAPScoreCalculator.CalculateScore(state.LastClosePrice, sessResult);
            int rollVwapScore = VWAPScoreCalculator.CalculateScore(state.LastClosePrice, rollResult);

            // Debug logging for first few calculations per symbol to diagnose VWAP score issue
            if (state.Row.Strike == 23600 && state.Type == "CE")
            {
                _log.Debug($"[VWAP Debug] {state.Symbol} Price={state.LastClosePrice:F2}, " +
                    $"SessVWAP={sessResult.VWAP:F2}, SessSD={sessResult.StdDev:F2}, " +
                    $"Sess1SD={sessResult.Upper1SD:F2}/{sessResult.Lower1SD:F2}, " +
                    $"Sess2SD={sessResult.Upper2SD:F2}/{sessResult.Lower2SD:F2}, " +
                    $"SessScore={sessVwapScore}, RollScore={rollVwapScore}, IsValid={sessResult.IsValid}");
            }

            // Update row based on type
            if (state.Type == "CE")
            {
                // HVN metrics
                state.Row.CEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                state.Row.CEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                state.Row.CETrendSess = sessTrend;
                state.Row.CETrendSessTime = sessTrendTime;

                state.Row.CEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                state.Row.CEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                state.Row.CETrendRoll = rollTrend;
                state.Row.CETrendRollTime = rollTrendTime;

                // CD Momentum metrics (smaMomentum style)
                state.Row.CECDMomo = FormatMomentum(cdMomoResult.Momentum);
                state.Row.CECDSmooth = FormatMomentum(cdMomoResult.Smooth);

                // Price Momentum metrics (smaMomentum style)
                state.Row.CEPriceMomo = FormatMomentum(priceMomoResult.Momentum);
                state.Row.CEPriceSmooth = FormatMomentum(priceMomoResult.Smooth);

                // VWAP Score metrics
                state.Row.CEVwapScoreSess = sessVwapScore;
                state.Row.CEVwapScoreRoll = rollVwapScore;
            }
            else
            {
                // HVN metrics
                state.Row.PEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                state.Row.PEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                state.Row.PETrendSess = sessTrend;
                state.Row.PETrendSessTime = sessTrendTime;

                state.Row.PEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                state.Row.PEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                state.Row.PETrendRoll = rollTrend;
                state.Row.PETrendRollTime = rollTrendTime;

                // CD Momentum metrics (smaMomentum style)
                state.Row.PECDMomo = FormatMomentum(cdMomoResult.Momentum);
                state.Row.PECDSmooth = FormatMomentum(cdMomoResult.Smooth);

                // Price Momentum metrics (smaMomentum style)
                state.Row.PEPriceMomo = FormatMomentum(priceMomoResult.Momentum);
                state.Row.PEPriceSmooth = FormatMomentum(priceMomoResult.Smooth);

                // VWAP Score metrics
                state.Row.PEVwapScoreSess = sessVwapScore;
                state.Row.PEVwapScoreRoll = rollVwapScore;
            }
        }

        /// <summary>
        /// Formats momentum value for display (K for thousands, M for millions).
        /// </summary>
        private string FormatMomentum(double momentum)
        {
            if (Math.Abs(momentum) >= 1_000_000)
                return (momentum / 1_000_000.0).ToString("F1") + "M";
            else if (Math.Abs(momentum) >= 1_000)
                return (momentum / 1_000.0).ToString("F1") + "K";
            else
                return momentum.ToString("F1");
        }

        private HvnTrend DetermineTrend(VPResult result)
        {
            if (!result.IsValid) return HvnTrend.Neutral;

            if (result.HVNBuyCount > result.HVNSellCount)
                return HvnTrend.Bullish;
            else if (result.HVNSellCount > result.HVNBuyCount)
                return HvnTrend.Bearish;
            else
                return HvnTrend.Neutral;
        }

        private void OnOptionPriceBatch(IList<OptionPriceUpdate> batch)
        {
            foreach (var update in batch)
            {
                if (_symbolToRowMap.TryGetValue(update.Symbol, out var map))
                {
                    string timeStr = update.Timestamp.ToString("HH:mm:ss");
                    if (map.type == "CE")
                    {
                        map.row.CELTP = update.Price.ToString("F2");
                        map.row.CETickTime = timeStr;
                    }
                    else
                    {
                        map.row.PELTP = update.Price.ToString("F2");
                        map.row.PETickTime = timeStr;
                    }
                }
            }
        }

        /// <summary>
        /// Determines which date's data to process based on current market phase:
        /// - PreMarket (before 9:15 on a trading day): use prior working day
        /// - MarketHours/PostMarket on a trading day: use today
        /// - Non-trading day (weekend/holiday): use prior working day
        /// </summary>
        private DateTime GetTargetDataDate()
        {
            DateTime today = DateTime.Today;

            // If today is not a trading day (weekend or holiday), use prior working day
            if (DateTimeHelper.IsNonTradingDay(today))
            {
                return HolidayCalendarService.Instance.GetPriorWorkingDay(today);
            }

            // If pre-market (before 9:15 AM), use prior working day
            if (DateTimeHelper.IsPreMarket())
            {
                return HolidayCalendarService.Instance.GetPriorWorkingDay(today);
            }

            // During market hours or post-market on a trading day, use today
            return today;
        }

        private void ClearAllVPStates()
        {
            foreach (var state in _vpStates.Values)
            {
                try
                {
                    state.Dispose();
                }
                catch { }
            }
            _vpStates.Clear();
        }

        public void Dispose()
        {
            StopServices();
        }
    }
}
