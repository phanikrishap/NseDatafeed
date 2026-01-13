using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;

// OptionVPState is in Models namespace
using OptionVPState = ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models.OptionVPState;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Singleton compute service that owns all option VP states independently of UI.
    /// Range bar subscriptions and VP computations live here and are NOT affected by UI changes.
    /// The ViewModel only queries this service for display data.
    ///
    /// Architecture:
    /// - ComputeService owns ALL VP states (~120 strikes, not just 21)
    /// - VP states persist across ATM changes
    /// - ViewModel queries GetRowsAroundATM() for display filtering
    /// - Sub-processors handle specific concerns (VP, Simulation)
    /// </summary>
    public class OptionSignalsComputeService : IDisposable
    {
        private static readonly Lazy<OptionSignalsComputeService> _instance =
            new Lazy<OptionSignalsComputeService>(() => new OptionSignalsComputeService());
        public static OptionSignalsComputeService Instance => _instance.Value;

        // State storage - ALL strikes, not just 21 around ATM
        private readonly ConcurrentDictionary<string, OptionVPState> _vpStates = new ConcurrentDictionary<string, OptionVPState>();
        private readonly ConcurrentDictionary<string, OptionSignalsRow> _rowsByStrike = new ConcurrentDictionary<string, OptionSignalsRow>();
        private readonly ConcurrentDictionary<string, string> _symbolToStrikeKey = new ConcurrentDictionary<string, string>();
        private readonly object _initLock = new object();
        private CompositeDisposable _subscriptions;

        // Logger
        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        // Current market context
        private string _currentUnderlying;
        private DateTime? _currentExpiry;
        private int _strikeStep = 50;
        private bool _isInitialized;

        // Sub-processors
        private readonly OptionDataSynchronizer _dataSynchronizer;
        private readonly OptionVPProcessor _vpProcessor;
        private readonly OptionSimulationProcessor _simProcessor;

        // Event fired when a strike's data updates (for UI refresh)
        public event EventHandler<StrikeDataUpdatedEventArgs> StrikeDataUpdated;

        // Signals Orchestrator - owned by compute service for signal evaluation
        public SignalsOrchestrator SignalsOrchestrator { get; private set; }

        // Properties
        public bool IsInitialized => _isInitialized;
        public string CurrentUnderlying => _currentUnderlying;
        public DateTime? CurrentExpiry => _currentExpiry;
        public int StrikeStep => _strikeStep;
        public int VPStateCount => _vpStates.Count;

        private OptionSignalsComputeService()
        {
            _subscriptions = new CompositeDisposable();

            // Initialize processors
            _dataSynchronizer = new OptionDataSynchronizer();
            _vpProcessor = new OptionVPProcessor();
            _simProcessor = new OptionSimulationProcessor(_vpProcessor);

            // Wire up processor dependencies
            _vpProcessor.SetSimulationProcessor(_simProcessor);
        }

        /// <summary>
        /// Initialize the compute service for a given underlying and expiry.
        /// Loads ALL strikes from the option chain (not just 21 around ATM).
        /// This is called once when option data becomes available.
        /// </summary>
        public async Task InitializeAsync(string underlying, DateTime expiry, List<MappedInstrument> allOptions)
        {
            if (allOptions == null || allOptions.Count == 0) return;

            lock (_initLock)
            {
                // Skip if already initialized for same underlying/expiry
                if (_isInitialized && _currentUnderlying == underlying && _currentExpiry == expiry)
                {
                    _log.Debug($"[ComputeService] Already initialized for {underlying} {expiry:dd-MMM-yyyy}");
                    return;
                }

                // If switching underlying/expiry, clear old state
                if (_currentUnderlying != underlying || _currentExpiry != expiry)
                {
                    ClearAllStates();
                }

                _currentUnderlying = underlying;
                _currentExpiry = expiry;
            }

            _log.Info($"[ComputeService] Initializing for {underlying} {expiry:dd-MMM-yyyy} with {allOptions.Count} options");

            // Determine strike step from options
            var uniqueStrikes = allOptions.Where(o => o.strike.HasValue)
                .Select(o => (int)o.strike.Value)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            if (uniqueStrikes.Count >= 2)
                _strikeStep = uniqueStrikes[1] - uniqueStrikes[0];

            // Update VP processor market context
            decimal dynamicAtm = MarketAnalyzerLogic.Instance.GetATMStrike(underlying);
            double atmStrike = dynamicAtm > 0 ? (double)dynamicAtm : (uniqueStrikes.Count > 0 ? uniqueStrikes[uniqueStrikes.Count / 2] : 0);
            _vpProcessor.SetMarketContext(atmStrike, _strikeStep, expiry, underlying);

            // Sync historical data for all symbols using the data synchronizer
            _log.Info($"[ComputeService] Starting historical sync for {allOptions.Count} symbols");
            var syncResult = await _dataSynchronizer.SynchronizeAsync(allOptions, CancellationToken.None, 30);
            _log.Info($"[ComputeService] Historical sync complete: {syncResult.SuccessCount}/{syncResult.TotalSymbols} in {syncResult.Duration.TotalSeconds:F1}s");

            // Create VP states for all options
            foreach (var option in allOptions)
            {
                if (string.IsNullOrEmpty(option.symbol) || !option.strike.HasValue) continue;

                int strike = (int)option.strike.Value;
                string type = option.option_type;

                // Create row if not exists
                var row = GetOrCreateRow(strike);

                // Set symbol on row
                if (type == "CE")
                    row.CESymbol = option.symbol;
                else
                    row.PESymbol = option.symbol;

                // Create VP state
                CreateVPState(option.symbol, strike, type, row);
            }

            _isInitialized = true;
            _log.Info($"[ComputeService] Initialized {_vpStates.Count} VP states for {_rowsByStrike.Count} strikes");
        }

        /// <summary>
        /// Gets or creates an OptionSignalsRow for a strike.
        /// </summary>
        private OptionSignalsRow GetOrCreateRow(int strike)
        {
            string rowKey = $"STRIKE_{strike}";
            return _rowsByStrike.GetOrAdd(rowKey, _ => new OptionSignalsRow { Strike = strike });
        }

        /// <summary>
        /// Gets all rows for strikes within +-N of the given ATM strike.
        /// Used by ViewModel to get display data WITHOUT resetting computation.
        /// This is the key method that enables ATM changes to be display-only.
        /// </summary>
        public List<OptionSignalsRow> GetRowsAroundATM(int atmStrike, int strikeCount = 10)
        {
            var result = new List<OptionSignalsRow>();

            for (int i = -strikeCount; i <= strikeCount; i++)
            {
                int strike = atmStrike + (i * _strikeStep);
                string rowKey = $"STRIKE_{strike}";

                if (_rowsByStrike.TryGetValue(rowKey, out var row))
                {
                    row.IsATM = (strike == atmStrike);
                    result.Add(row);
                }
            }

            return result.OrderBy(r => r.Strike).ToList();
        }

        /// <summary>
        /// Gets all available strikes.
        /// </summary>
        public List<int> GetAllStrikes()
        {
            return _rowsByStrike.Values
                .Select(r => (int)r.Strike)
                .Distinct()
                .OrderBy(s => s)
                .ToList();
        }

        /// <summary>
        /// Gets the VP state for a symbol (if exists).
        /// </summary>
        public OptionVPState GetVPState(string symbol)
        {
            return _vpStates.TryGetValue(symbol, out var state) ? state : null;
        }

        /// <summary>
        /// Gets the row for a symbol (if exists).
        /// </summary>
        public OptionSignalsRow GetRowForSymbol(string symbol)
        {
            if (_symbolToStrikeKey.TryGetValue(symbol, out var rowKey))
            {
                if (_rowsByStrike.TryGetValue(rowKey, out var row))
                    return row;
            }
            return null;
        }

        /// <summary>
        /// Sets the SignalsOrchestrator for signal evaluation.
        /// </summary>
        public void SetSignalsOrchestrator(SignalsOrchestrator orchestrator)
        {
            SignalsOrchestrator = orchestrator;
            _vpProcessor.SignalsOrchestrator = orchestrator;
        }

        /// <summary>
        /// Updates market context for VP processor and SignalsOrchestrator.
        /// </summary>
        public void SetMarketContext(double atmStrike, int strikeStep, DateTime? expiry, string underlying)
        {
            _vpProcessor.SetMarketContext(atmStrike, strikeStep, expiry, underlying);
            SignalsOrchestrator?.SetMarketContext(atmStrike, strikeStep, expiry, underlying);
        }

        /// <summary>
        /// Processes a simulated tick for a symbol.
        /// Delegates to simulation processor.
        /// </summary>
        public void ProcessSimulatedTick(string symbol, double price, DateTime tickTime)
        {
            if (_vpStates.TryGetValue(symbol, out var state))
            {
                _simProcessor.ProcessSimulatedTick(state, price, tickTime);
            }
        }

        /// <summary>
        /// Updates signal price for a symbol.
        /// </summary>
        public void UpdateSignalPrice(string symbol, double price)
        {
            SignalsOrchestrator?.UpdateSignalPrice(symbol, price);
        }

        /// <summary>
        /// Creates VP state and subscriptions for a single option symbol.
        /// </summary>
        private void CreateVPState(string symbol, int strike, string type, OptionSignalsRow row)
        {
            if (_vpStates.ContainsKey(symbol))
            {
                _log.Debug($"[ComputeService] VP state already exists for {symbol}");
                return;
            }

            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null)
            {
                _log.Warn($"[ComputeService] Instrument not found for {symbol}");
                return;
            }

            var state = new OptionVPState
            {
                Symbol = symbol,
                Type = type,
                Row = row,
                BarHistory = new OptionBarHistory(symbol, strike, type, 256)
            };

            // Track symbol to strike mapping
            string rowKey = $"STRIKE_{strike}";
            _symbolToStrikeKey[symbol] = rowKey;

            // Setup Rx pipeline: wait for both tick and range data, then process
            var bothReadyPipeline = Observable.CombineLatest(
                state.TickDataReady.Where(r => r),
                state.RangeBarsReady.Where(r => r),
                (tickReady, rangeReady) => true
            )
            .Take(1)
            .Subscribe(_ =>
            {
                _log.Debug($"[ComputeService] {symbol} both tick and range data ready, processing historical");
                _vpProcessor.ProcessHistoricalData(state);

                // Fire update event after historical processing
                StrikeDataUpdated?.Invoke(this, new StrikeDataUpdatedEventArgs { Symbol = symbol });
            });

            state.Subscriptions.Add(bothReadyPipeline);

            // Setup real-time bar update pipeline
            var realTimeUpdatePipeline = state.RangeBarUpdates
                .Where(_ => state.LastRangeBarIndex >= 0)
                .Subscribe(e =>
                {
                    _vpProcessor.ProcessRealTimeBar(state, e);
                    StrikeDataUpdated?.Invoke(this, new StrikeDataUpdatedEventArgs { Symbol = symbol });
                });

            state.Subscriptions.Add(realTimeUpdatePipeline);

            // Request bar data on NT dispatcher
            NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                bool isSimMode = SimulationService.Instance.IsSimulationMode;
                DateTime? simDate = isSimMode ? SimulationService.Instance.CurrentConfig?.SimulationDate : null;

                BarsRequest tickRequest;
                BarsRequest rangeRequest;

                if (isSimMode && simDate.HasValue)
                {
                    DateTime priorDay = HolidayCalendarService.Instance.GetPriorWorkingDay(simDate.Value);
                    DateTime fromDate = priorDay.Date;
                    DateTime toDate = simDate.Value.Date.AddDays(1);

                    tickRequest = new BarsRequest(instrument, fromDate, toDate);
                    tickRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = BarsPeriodType.Tick,
                        Value = 1
                    };
                    tickRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    rangeRequest = new BarsRequest(instrument, fromDate, toDate);
                    rangeRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = (BarsPeriodType)7015, // RangeATR
                        Value = 1,
                        Value2 = 3,
                        BaseBarsPeriodValue = 1
                    };
                    rangeRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                }
                else
                {
                    tickRequest = new BarsRequest(instrument, 10000);
                    tickRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = BarsPeriodType.Tick,
                        Value = 1
                    };
                    tickRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    rangeRequest = new BarsRequest(instrument, 100);
                    rangeRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = (BarsPeriodType)7015, // RangeATR
                        Value = 1,
                        Value2 = 3,
                        BaseBarsPeriodValue = 1
                    };
                    rangeRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                }

                // Subscribe to range bar updates
                rangeRequest.Update += (s, e) =>
                {
                    if (!state.IsDisposed)
                    {
                        try { state.RangeBarUpdates.OnNext(e); }
                        catch (ObjectDisposedException) { }
                    }
                };

                // Request tick data
                tickRequest.Request((r, code, msg) =>
                {
                    if (code == ErrorCode.NoError)
                    {
                        int totalCount = r.Bars.Count;
                        if (totalCount > 0)
                        {
                            DateTime firstTime = r.Bars.GetTime(0);
                            DateTime lastTime = r.Bars.GetTime(totalCount - 1);
                            _log.Debug($"[ComputeService] TickBars OK: {symbol}, total={totalCount}, first={firstTime:MM-dd HH:mm:ss}, last={lastTime:MM-dd HH:mm:ss}");
                        }
                    }
                    state.TickDataReady.OnNext(true);
                });

                state.TickBarsRequest = tickRequest;

                // Request range bar data
                rangeRequest.Request((r, code, msg) =>
                {
                    if (code == ErrorCode.NoError)
                    {
                        int totalCount = r.Bars.Count;
                        if (totalCount > 0)
                        {
                            DateTime firstTime = r.Bars.GetTime(0);
                            DateTime lastTime = r.Bars.GetTime(totalCount - 1);
                            _log.Debug($"[ComputeService] RangeBars OK: {symbol}, total={totalCount}, first={firstTime:MM-dd HH:mm:ss}, last={lastTime:MM-dd HH:mm:ss}");
                        }
                    }
                    state.RangeBarsReady.OnNext(true);
                });

                state.RangeBarsRequest = rangeRequest;
            });

            _vpStates[symbol] = state;
        }

        /// <summary>
        /// Clears all VP states and subscriptions.
        /// Only called when underlying/expiry changes, NOT on ATM shifts.
        /// </summary>
        public void ClearAllStates()
        {
            _log.Info("[ComputeService] Clearing all states");

            foreach (var state in _vpStates.Values)
            {
                state.Dispose();
            }
            _vpStates.Clear();
            _rowsByStrike.Clear();
            _symbolToStrikeKey.Clear();
            _isInitialized = false;
        }

        public void Dispose()
        {
            ClearAllStates();
            _subscriptions?.Dispose();
        }
    }

    /// <summary>
    /// Event args for strike data updates.
    /// </summary>
    public class StrikeDataUpdatedEventArgs : EventArgs
    {
        public string Symbol { get; set; }
    }
}
