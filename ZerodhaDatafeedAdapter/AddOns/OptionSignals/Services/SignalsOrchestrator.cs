using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Snapshot of the underlying (Nifty Futures) state for signal evaluation.
    /// </summary>
    public class UnderlyingStateSnapshot
    {
        public string Symbol { get; set; }
        public double Price { get; set; }
        public DateTime Time { get; set; }

        // Session VP metrics
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public int SessHvnB { get; set; }
        public int SessHvnS { get; set; }
        public HvnTrend SessTrend { get; set; }

        // Rolling VP metrics
        public double RollingPOC { get; set; }
        public double RollingVAH { get; set; }
        public double RollingVAL { get; set; }
        public int RollHvnB { get; set; }
        public int RollHvnS { get; set; }
        public HvnTrend RollTrend { get; set; }

        // Relative metrics
        public double RelHvnBuySess { get; set; }
        public double RelHvnSellSess { get; set; }
        public double RelHvnBuyRoll { get; set; }
        public double RelHvnSellRoll { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Snapshot of an option's current state for signal evaluation.
    /// </summary>
    public class OptionStateSnapshot
    {
        public string Symbol { get; set; }
        public double Strike { get; set; }
        public string OptionType { get; set; } // CE or PE
        public Moneyness Moneyness { get; set; }
        public int DTE { get; set; } // Days to expiry
        public double LastPrice { get; set; }
        public DateTime LastPriceTime { get; set; }
        public double RangeBarClosePrice { get; set; }
        public DateTime RangeBarCloseTime { get; set; }

        // VP Metrics (Session)
        public int SessHvnB { get; set; }
        public int SessHvnS { get; set; }
        public HvnTrend SessTrend { get; set; }
        public DateTime? SessTrendOnsetTime { get; set; }

        // VP Metrics (Rolling)
        public int RollHvnB { get; set; }
        public int RollHvnS { get; set; }
        public HvnTrend RollTrend { get; set; }
        public DateTime? RollTrendOnsetTime { get; set; }

        // Momentum Metrics
        public double CDMomentum { get; set; }
        public double CDSmooth { get; set; }
        public MomentumBias CDBias { get; set; }
        public double PriceMomentum { get; set; }
        public double PriceSmooth { get; set; }
        public MomentumBias PriceBias { get; set; }

        // VWAP Metrics
        public int VwapScoreSess { get; set; }
        public int VwapScoreRoll { get; set; }
        public double SessionVWAP { get; set; }
        public double RollingVWAP { get; set; }
    }

    /// <summary>
    /// Base class for trading strategies.
    /// </summary>
    public abstract class BaseStrategy
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Evaluates all option states and returns any signals to generate.
        /// </summary>
        /// <param name="optionStates">Current state snapshots for all options</param>
        /// <param name="barHistories">Historical bar data for each option symbol</param>
        /// <param name="atmStrike">Current ATM strike</param>
        /// <param name="strikeStep">Strike step size</param>
        /// <param name="currentTime">Bar close time being evaluated</param>
        /// <param name="existingSignals">Existing signals for deduplication</param>
        public abstract List<SignalRow> Evaluate(
            IReadOnlyDictionary<string, OptionStateSnapshot> optionStates,
            IReadOnlyDictionary<string, OptionBarHistory> barHistories,
            double atmStrike,
            int strikeStep,
            DateTime currentTime,
            IReadOnlyList<SignalRow> existingSignals);
    }

    /// <summary>
    /// Sample Strategy: Enter ATM, ITM1, OTM1 strikes when Session HVN Buy > Session HVN Sell.
    /// - Buys CE options when Session HVN Buy > Sell (bullish accumulation)
    /// - Buys PE options when Session HVN Sell > Buy (bearish accumulation)
    /// </summary>
    public class HvnBuySellStrategy : BaseStrategy
    {
        public override string Name => "HvnBuySell";
        public override string Description => "Entry on Session HVN B > S (ATM, ITM1, OTM1)";

        private readonly HashSet<string> _signalsGeneratedToday = new HashSet<string>();

        // Replay mode - allows repetitive trades on same symbol
        private bool _isReplayMode = false;

        // Track last signal state per symbol to only generate on state change
        private readonly Dictionary<string, bool> _lastSignalState = new Dictionary<string, bool>();

        private IReadOnlyDictionary<string, OptionBarHistory> _currentBarHistories;

        // Market hours constants (IST)
        private static readonly TimeSpan MARKET_OPEN = new TimeSpan(9, 15, 0);
        private static readonly TimeSpan MARKET_CLOSE = new TimeSpan(15, 30, 0);

        /// <summary>
        /// Calculates quantity (lots) based on entry exposure from ExecutionSettings.
        /// Quantity = EntryExposure / (LotSize * EntryPrice), capped at MaxLots.
        /// </summary>
        private int CalculateQuantity(double entryPrice, string symbol)
        {
            var execSettings = ZerodhaDatafeedAdapter.Services.Configuration.ConfigurationManager.Instance.ExecutionSettings;
            if (execSettings == null || entryPrice <= 0)
                return 1;

            // Get underlying from symbol (NIFTY25JAN... -> NIFTY, SENSEX26JAN... -> SENSEX)
            string underlying = symbol.StartsWith("SENSEX", StringComparison.OrdinalIgnoreCase) ? "SENSEX" : "NIFTY";
            int lotSize = Helpers.SymbolHelper.GetLotSize(underlying);

            return execSettings.CalculateLots((decimal)entryPrice, lotSize);
        }

        public override List<SignalRow> Evaluate(
            IReadOnlyDictionary<string, OptionStateSnapshot> optionStates,
            IReadOnlyDictionary<string, OptionBarHistory> barHistories,
            double atmStrike,
            int strikeStep,
            DateTime currentTime,
            IReadOnlyList<SignalRow> existingSignals)
        {
            _currentBarHistories = barHistories;
            var signals = new List<SignalRow>();

            // Market hours check: Only generate signals during market hours (9:15 AM to 3:30 PM IST)
            var timeOfDay = currentTime.TimeOfDay;
            if (timeOfDay < MARKET_OPEN || timeOfDay > MARKET_CLOSE)
            {
                return signals; // No signals outside market hours
            }

            // Define target strikes: ATM, ITM1 (ATM-step for CE, ATM+step for PE), OTM1 (ATM+step for CE, ATM-step for PE)
            var targetCEStrikes = new List<(double strike, Moneyness moneyness)>
            {
                (atmStrike, Moneyness.ATM),
                (atmStrike - strikeStep, Moneyness.ITM1),   // ITM for CE
                (atmStrike + strikeStep, Moneyness.OTM1)    // OTM for CE
            };

            var targetPEStrikes = new List<(double strike, Moneyness moneyness)>
            {
                (atmStrike, Moneyness.ATM),
                (atmStrike + strikeStep, Moneyness.ITM1),   // ITM for PE
                (atmStrike - strikeStep, Moneyness.OTM1)    // OTM for PE
            };

            // Evaluate CE options
            foreach (var (strike, moneyness) in targetCEStrikes)
            {
                var ceOption = optionStates.Values.FirstOrDefault(o =>
                    o.Strike == strike && o.OptionType == "CE");

                if (ceOption != null)
                {
                    var ceSignal = EvaluateCEOption(ceOption, moneyness, currentTime, existingSignals);
                    if (ceSignal != null)
                        signals.Add(ceSignal);
                }
            }

            // Evaluate PE options
            foreach (var (strike, moneyness) in targetPEStrikes)
            {
                var peOption = optionStates.Values.FirstOrDefault(o =>
                    o.Strike == strike && o.OptionType == "PE");

                if (peOption != null)
                {
                    var peSignal = EvaluatePEOption(peOption, moneyness, currentTime, existingSignals);
                    if (peSignal != null)
                        signals.Add(peSignal);
                }
            }

            return signals;
        }

        private SignalRow EvaluateCEOption(OptionStateSnapshot option, Moneyness moneyness,
            DateTime currentTime, IReadOnlyList<SignalRow> existingSignals)
        {
            // Strategy condition: Session HVN Buy > Session HVN Sell for CE (bullish accumulation)
            bool conditionMet = option.SessHvnB > option.SessHvnS && option.SessHvnB >= 1;
            string stateKey = $"CE_{option.Symbol}";

            if (_isReplayMode)
            {
                // In replay mode: generate signal on state change from false to true
                bool wasConditionMet = _lastSignalState.TryGetValue(stateKey, out bool lastState) && lastState;
                _lastSignalState[stateKey] = conditionMet;

                // Only generate if condition just became true (transition)
                if (!conditionMet || wasConditionMet)
                    return null;
            }
            else
            {
                // In real-time mode: use daily dedup
                string signalKey = $"{Name}_{option.Symbol}_{currentTime.Date:yyyyMMdd}";
                if (_signalsGeneratedToday.Contains(signalKey))
                    return null;

                // Check if there's already an active or pending signal for this symbol
                if (existingSignals.Any(s => s.Symbol == option.Symbol &&
                    (s.Status == SignalStatus.Active || s.Status == SignalStatus.Pending)))
                    return null;

                if (!conditionMet)
                    return null;

                _signalsGeneratedToday.Add(signalKey);
            }

            // Condition is met and we should generate a signal
            // Get entry price from bar history at signal time for accurate historical prices
            double entryPrice = option.RangeBarClosePrice; // Default to current
            if (_currentBarHistories != null &&
                _currentBarHistories.TryGetValue(option.Symbol, out var history))
            {
                var histPrice = history.GetPriceAtOrBefore(currentTime);
                if (histPrice.HasValue)
                    entryPrice = histPrice.Value;
            }

            // Calculate quantity based on entry exposure
            int quantity = CalculateQuantity(entryPrice, option.Symbol);

            // Signals are immediately Active with entry at historical price
            return new SignalRow
            {
                SignalId = SignalRow.GenerateSignalId(Name, option.Symbol, currentTime),
                SignalTime = currentTime,
                Symbol = option.Symbol,
                Strike = option.Strike,
                OptionType = option.OptionType,
                Moneyness = moneyness,
                Direction = SignalDirection.Long,
                Status = SignalStatus.Active,
                Quantity = quantity,
                EntryPrice = entryPrice,
                EntryTime = currentTime,
                CurrentPrice = option.RangeBarClosePrice, // Current price for P&L tracking
                StrategyName = Name,
                SignalReason = $"Sess HVN B({option.SessHvnB}) > S({option.SessHvnS})",
                DTE = option.DTE,
                SessHvnB = option.SessHvnB,
                SessHvnS = option.SessHvnS,
                RollHvnB = option.RollHvnB,
                RollHvnS = option.RollHvnS,
                CDMomentum = option.CDMomentum,
                CDSmooth = option.CDSmooth,
                PriceMomentum = option.PriceMomentum,
                PriceSmooth = option.PriceSmooth,
                VwapScoreSess = option.VwapScoreSess,
                VwapScoreRoll = option.VwapScoreRoll
            };
        }

        private SignalRow EvaluatePEOption(OptionStateSnapshot option, Moneyness moneyness,
            DateTime currentTime, IReadOnlyList<SignalRow> existingSignals)
        {
            // Strategy condition: Session HVN Sell > Session HVN Buy for PE (bearish accumulation)
            bool conditionMet = option.SessHvnS > option.SessHvnB && option.SessHvnS >= 1;
            string stateKey = $"PE_{option.Symbol}";

            if (_isReplayMode)
            {
                // In replay mode: generate signal on state change from false to true
                bool wasConditionMet = _lastSignalState.TryGetValue(stateKey, out bool lastState) && lastState;
                _lastSignalState[stateKey] = conditionMet;

                // Only generate if condition just became true (transition)
                if (!conditionMet || wasConditionMet)
                    return null;
            }
            else
            {
                // In real-time mode: use daily dedup
                string signalKey = $"{Name}_{option.Symbol}_{currentTime.Date:yyyyMMdd}";
                if (_signalsGeneratedToday.Contains(signalKey))
                    return null;

                // Check if there's already an active or pending signal for this symbol
                if (existingSignals.Any(s => s.Symbol == option.Symbol &&
                    (s.Status == SignalStatus.Active || s.Status == SignalStatus.Pending)))
                    return null;

                if (!conditionMet)
                    return null;

                _signalsGeneratedToday.Add(signalKey);
            }

            // Condition is met and we should generate a signal
            // Get entry price from bar history at signal time for accurate historical prices
            double entryPrice = option.RangeBarClosePrice; // Default to current
            if (_currentBarHistories != null &&
                _currentBarHistories.TryGetValue(option.Symbol, out var history))
            {
                var histPrice = history.GetPriceAtOrBefore(currentTime);
                if (histPrice.HasValue)
                    entryPrice = histPrice.Value;
            }

            // Calculate quantity based on entry exposure
            int quantity = CalculateQuantity(entryPrice, option.Symbol);

            // Signals are immediately Active with entry at historical price
            return new SignalRow
            {
                SignalId = SignalRow.GenerateSignalId(Name, option.Symbol, currentTime),
                SignalTime = currentTime,
                Symbol = option.Symbol,
                Strike = option.Strike,
                OptionType = option.OptionType,
                Moneyness = moneyness,
                Direction = SignalDirection.Long, // Buying puts
                Status = SignalStatus.Active,
                Quantity = quantity,
                EntryPrice = entryPrice,
                EntryTime = currentTime,
                CurrentPrice = option.RangeBarClosePrice, // Current price for P&L tracking
                StrategyName = Name,
                SignalReason = $"Sess HVN S({option.SessHvnS}) > B({option.SessHvnB})",
                DTE = option.DTE,
                SessHvnB = option.SessHvnB,
                SessHvnS = option.SessHvnS,
                RollHvnB = option.RollHvnB,
                RollHvnS = option.RollHvnS,
                CDMomentum = option.CDMomentum,
                CDSmooth = option.CDSmooth,
                PriceMomentum = option.PriceMomentum,
                PriceSmooth = option.PriceSmooth,
                VwapScoreSess = option.VwapScoreSess,
                VwapScoreRoll = option.VwapScoreRoll
            };
        }

        /// <summary>
        /// Resets the daily signal tracking (call on new trading day).
        /// </summary>
        public void ResetDaily()
        {
            _signalsGeneratedToday.Clear();
            _lastSignalState.Clear();
        }

        /// <summary>
        /// Sets replay mode on/off.
        /// In replay mode, allows repetitive trades on same symbol (generates on each condition match).
        /// </summary>
        public void SetReplayMode(bool enabled)
        {
            _isReplayMode = enabled;
            if (enabled)
            {
                _signalsGeneratedToday.Clear();
                _lastSignalState.Clear();
            }
        }
    }

    /// <summary>
    /// Orchestrates signal generation by running strategies on option state updates.
    /// Runs on every tick/bar update and manages the lifecycle of signals.
    /// </summary>
    public class SignalsOrchestrator : IDisposable
    {
        /// <summary>
        /// Fired when signal statistics change (signal added, closed, or P&L updated).
        /// Subscribe to this to update UI summary metrics.
        /// </summary>
        public event EventHandler SignalStatsChanged;

        private readonly ObservableCollection<SignalRow> _signals;
        private readonly Dictionary<string, OptionStateSnapshot> _optionStates = new Dictionary<string, OptionStateSnapshot>();
        private readonly Dictionary<string, OptionBarHistory> _barHistories = new Dictionary<string, OptionBarHistory>();
        private readonly List<BaseStrategy> _strategies = new List<BaseStrategy>();
        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new object();

        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        // Underlying state
        private UnderlyingStateSnapshot _underlyingState;
        private UnderlyingBarHistory _underlyingBarHistory;
        private DateTime _lastUnderlyingLogTime = DateTime.MinValue;
        private readonly TimeSpan _underlyingLogInterval = TimeSpan.FromSeconds(30); // Log underlying every 30s

        private double _atmStrike;
        private int _strikeStep = 50;
        private DateTime? _selectedExpiry;
        private string _underlying = "NIFTY"; // Underlying for dynamic ATM lookup
        private DateTime _lastEvaluationTime = DateTime.MinValue;
        private readonly TimeSpan _minEvaluationInterval = TimeSpan.FromMilliseconds(500); // Throttle evaluations

        // Replay mode - disables duplicate signal check and throttling
        private bool _isReplayMode = false;

        // Position tracking for auto-exit (CE and PE tracked separately)
        private bool _autoExitEnabled = true;
        private SignalRow _activeCELongPosition;
        private SignalRow _activePELongPosition;

        public SignalsOrchestrator(ObservableCollection<SignalRow> signals, Dispatcher dispatcher)
        {
            _signals = signals;
            _dispatcher = dispatcher;

            // Initialize underlying bar history (default to NIFTY)
            _underlyingBarHistory = new UnderlyingBarHistory("NIFTY_I", 256);

            // Register default strategies
            _strategies.Add(new HvnBuySellStrategy());

            _log.Info("[SignalsOrchestrator] Initialized with default strategies");
            TerminalService.Instance.Info("SignalsOrchestrator initialized");
        }

        /// <summary>
        /// Resets the underlying bar history with a new symbol.
        /// Call this when the underlying changes (e.g., switching from NIFTY to SENSEX).
        /// </summary>
        public void ResetUnderlyingHistory(string symbol)
        {
            lock (_lock)
            {
                _underlyingBarHistory = new UnderlyingBarHistory(symbol, 256);
                _underlyingState = null;
                _log.Info($"[SignalsOrchestrator] Underlying history reset for {symbol}");
            }
        }

        /// <summary>
        /// Gets the current underlying state.
        /// </summary>
        public UnderlyingStateSnapshot UnderlyingState => _underlyingState;

        /// <summary>
        /// Gets or sets whether replay mode is active.
        /// In replay mode, duplicate signal checks are disabled and throttling is bypassed.
        /// </summary>
        public bool IsReplayMode => _isReplayMode;

        /// <summary>
        /// Sets replay mode on/off.
        /// </summary>
        public void SetReplayMode(bool enabled)
        {
            _isReplayMode = enabled;
            _log.Info($"[SignalsOrchestrator] Replay mode: {enabled}");

            if (enabled)
            {
                // Clear existing signals when entering replay/simulation mode
                ClearAllSignals();

                // Reset strategy daily tracking when entering replay mode
                foreach (var strategy in _strategies)
                {
                    if (strategy is HvnBuySellStrategy hvnStrategy)
                    {
                        hvnStrategy.ResetDaily();
                        hvnStrategy.SetReplayMode(true);
                    }
                }

                // Clear position tracking
                _activeCELongPosition = null;
                _activePELongPosition = null;
            }
            else
            {
                foreach (var strategy in _strategies)
                {
                    if (strategy is HvnBuySellStrategy hvnStrategy)
                    {
                        hvnStrategy.SetReplayMode(false);
                    }
                }
            }
        }

        /// <summary>
        /// Clears all signals from the collection.
        /// </summary>
        public void ClearAllSignals()
        {
            _dispatcher.InvokeAsync(() =>
            {
                _signals.Clear();
                _log.Info("[SignalsOrchestrator] All signals cleared");
                TerminalService.Instance.Info("Signals cleared for new session");
            });
        }

        /// <summary>
        /// Gets the current time - uses simulation time when in replay mode, otherwise real time.
        /// </summary>
        private DateTime GetCurrentTime()
        {
            if (_isReplayMode && SimulationService.Instance.IsSimulationActive)
            {
                var simTime = SimulationService.Instance.CurrentSimTime;
                return simTime != DateTime.MinValue ? simTime : DateTime.Now;
            }
            return DateTime.Now;
        }

        /// <summary>
        /// Enables or disables auto-exit of opposite positions.
        /// </summary>
        public void EnableAutoExit(bool enabled)
        {
            _autoExitEnabled = enabled;
            _log.Info($"[SignalsOrchestrator] Auto-exit: {enabled}");
        }

        /// <summary>
        /// Gets the underlying bar history for historical lookups.
        /// </summary>
        public UnderlyingBarHistory UnderlyingBarHistory => _underlyingBarHistory;

        /// <summary>
        /// Sets market context for signal evaluation.
        /// </summary>
        public void SetMarketContext(double atmStrike, int strikeStep, DateTime? selectedExpiry, string underlying = null)
        {
            lock (_lock)
            {
                _atmStrike = atmStrike;
                _strikeStep = strikeStep;
                _selectedExpiry = selectedExpiry;
                if (!string.IsNullOrEmpty(underlying))
                {
                    _underlying = underlying;
                }
            }
            TerminalService.Instance.Info($"Market context set: Underlying={_underlying}, ATM={atmStrike}, Step={strikeStep}, Expiry={selectedExpiry?.ToString("dd-MMM") ?? "N/A"}");
        }

        /// <summary>
        /// Updates the underlying (Nifty Futures) state. Called when NiftyFuturesMetrics update.
        /// </summary>
        public void UpdateUnderlyingState(UnderlyingStateSnapshot snapshot)
        {
            lock (_lock)
            {
                _underlyingState = snapshot;

                // Store in bar history
                if (snapshot.IsValid)
                {
                    var barSnapshot = new UnderlyingBarSnapshot
                    {
                        BarTime = snapshot.Time,
                        ClosePrice = snapshot.Price,
                        POC = snapshot.POC,
                        VAH = snapshot.VAH,
                        VAL = snapshot.VAL,
                        VWAP = snapshot.VWAP,
                        SessHvnB = snapshot.SessHvnB,
                        SessHvnS = snapshot.SessHvnS,
                        SessTrend = snapshot.SessTrend,
                        RollingPOC = snapshot.RollingPOC,
                        RollingVAH = snapshot.RollingVAH,
                        RollingVAL = snapshot.RollingVAL,
                        RollHvnB = snapshot.RollHvnB,
                        RollHvnS = snapshot.RollHvnS,
                        RollTrend = snapshot.RollTrend,
                        RelHvnBuySess = snapshot.RelHvnBuySess,
                        RelHvnSellSess = snapshot.RelHvnSellSess,
                        RelHvnBuyRoll = snapshot.RelHvnBuyRoll,
                        RelHvnSellRoll = snapshot.RelHvnSellRoll
                    };
                    _underlyingBarHistory.AddBar(barSnapshot);
                }
            }

            // Log to terminal periodically
            if (snapshot.IsValid && (DateTime.Now - _lastUnderlyingLogTime) >= _underlyingLogInterval)
            {
                _lastUnderlyingLogTime = DateTime.Now;
                string trend = snapshot.SessTrend == HvnTrend.Bullish ? "BULL" :
                               snapshot.SessTrend == HvnTrend.Bearish ? "BEAR" : "NEU";
                string rollTrend = snapshot.RollTrend == HvnTrend.Bullish ? "BULL" :
                                   snapshot.RollTrend == HvnTrend.Bearish ? "BEAR" : "NEU";

                TerminalService.Instance.Underlying(
                    $"NIFTY @ {snapshot.Price:F2} | Sess: HVN B={snapshot.SessHvnB} S={snapshot.SessHvnS} [{trend}] | " +
                    $"Roll: HVN B={snapshot.RollHvnB} S={snapshot.RollHvnS} [{rollTrend}] | " +
                    $"POC={snapshot.POC:F0} VAH={snapshot.VAH:F0} VAL={snapshot.VAL:F0}");
            }
        }

        /// <summary>
        /// Updates an option's state snapshot. Called on every VP recalculation.
        /// </summary>
        /// <param name="snapshot">Current state snapshot</param>
        /// <param name="barHistory">Optional bar history for historical price lookups</param>
        public void UpdateOptionState(OptionStateSnapshot snapshot, OptionBarHistory barHistory = null)
        {
            lock (_lock)
            {
                _optionStates[snapshot.Symbol] = snapshot;
                if (barHistory != null)
                {
                    _barHistories[snapshot.Symbol] = barHistory;
                }
            }

            // Always update current price for active signals (for live P&L tracking)
            UpdateSignalPrice(snapshot.Symbol, snapshot.RangeBarClosePrice);

            // In replay mode, evaluate on every update (no throttling)
            // In real-time mode, throttle strategy evaluations
            if (_isReplayMode || (DateTime.Now - _lastEvaluationTime) >= _minEvaluationInterval)
            {
                EvaluateStrategies(snapshot.RangeBarCloseTime);
            }
        }

        /// <summary>
        /// Gets the price from bar history at a specific time for accurate historical entry prices.
        /// </summary>
        public double? GetHistoricalPrice(string symbol, DateTime barTime)
        {
            lock (_lock)
            {
                if (_barHistories.TryGetValue(symbol, out var history))
                {
                    return history.GetPriceAtOrBefore(barTime);
                }
                return null;
            }
        }

        /// <summary>
        /// Gets the full bar snapshot from history at a specific time.
        /// </summary>
        public OptionBarSnapshot? GetHistoricalBarSnapshot(string symbol, DateTime barTime)
        {
            lock (_lock)
            {
                if (_barHistories.TryGetValue(symbol, out var history))
                {
                    return history.GetAtOrBefore(barTime);
                }
                return null;
            }
        }

        /// <summary>
        /// Updates price for existing signals and checks stoploss exposure.
        /// </summary>
        // Throttle logging - only log every N calls
        private int _updatePriceCallCount = 0;
        private const int LOG_EVERY_N_CALLS = 100;

        public void UpdateSignalPrice(string symbol, double price)
        {
            bool statsChanged = false;
            _updatePriceCallCount++;

            // Log once at start to confirm method is being called
            if (_updatePriceCallCount == 1)
            {
                _log.Info($"[SignalsOrchestrator] UpdateSignalPrice called for first time: symbol={symbol}, price={price:F2}, activeSignals={_signals.Count(s => s.Status == SignalStatus.Active)}");
            }

            // Use Invoke instead of InvokeAsync for synchronous execution in simulation mode
            // This ensures P&L updates happen immediately during replay
            Action updateAction = () =>
            {
                var execSettings = ZerodhaDatafeedAdapter.Services.Configuration.ConfigurationManager.Instance.ExecutionSettings;

                // Find all active signals for this symbol
                var activeSignals = _signals.Where(s =>
                    s.Symbol == symbol && s.Status == SignalStatus.Active).ToList();

                // Log periodic updates to trace price update flow
                if (_updatePriceCallCount % LOG_EVERY_N_CALLS == 0)
                {
                    _log.Info($"[SignalsOrchestrator] UpdateSignalPrice #{_updatePriceCallCount}: symbol={symbol}, price={price:F2}, matchedCount={activeSignals.Count}, totalActive={_signals.Count(s => s.Status == SignalStatus.Active)}");
                }

                // Debug: Log when we have active signals but no matches found
                if (activeSignals.Count == 0 && _signals.Any(s => s.Status == SignalStatus.Active))
                {
                    // Log first few mismatches at INFO level
                    if (_updatePriceCallCount <= 5)
                    {
                        var allActiveSymbols = _signals.Where(s => s.Status == SignalStatus.Active)
                            .Select(s => s.Symbol).Distinct().Take(5);
                        _log.Info($"[SignalsOrchestrator] UpdateSignalPrice: No match for '{symbol}', active symbols: [{string.Join(", ", allActiveSymbols)}]");
                    }
                }

                foreach (var signal in activeSignals)
                {
                    double oldPrice = signal.CurrentPrice;
                    signal.CurrentPrice = price;
                    statsChanged = true; // P&L changed

                    // Log successful price updates periodically
                    if (_updatePriceCallCount % 500 == 0 || (_updatePriceCallCount <= 10 && Math.Abs(oldPrice - price) > 0.01))
                    {
                        _log.Info($"[SignalsOrchestrator] Price UPDATED: {symbol} {oldPrice:F2} -> {price:F2}, UnrealizedPnL={signal.UnrealizedPnL:F2}, SignalId={signal.SignalId}");
                    }

                    // Check stoploss exposure - exit if loss exceeds configured threshold
                    if (execSettings != null && execSettings.IsStoplossTriggered((decimal)signal.UnrealizedPnL))
                    {
                        _log.Info($"[SignalsOrchestrator] STOPLOSS TRIGGERED: {signal.Symbol} UnrealizedPnL={signal.UnrealizedPnL:F2} exceeds StoplossExposure={execSettings.StoplossExposure}");
                        TerminalService.Instance.Signal($"[STOPLOSS] {signal.Symbol} closed at {price:F2} | Loss={signal.UnrealizedPnL:F2}");

                        // Close the signal
                        CloseSignal(signal.SignalId, price, "Stoploss exposure exceeded");
                    }
                }
            };

            // In replay mode, execute synchronously for immediate P&L updates
            // In live mode, use async to avoid blocking tick processing
            if (_isReplayMode)
            {
                if (_dispatcher.CheckAccess())
                {
                    updateAction();
                }
                else
                {
                    _dispatcher.Invoke(updateAction);
                }
            }
            else
            {
                _dispatcher.InvokeAsync(updateAction);
            }

            // Notify subscribers that stats may have changed
            if (statsChanged)
            {
                SignalStatsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Evaluates all strategies with current option states.
        /// </summary>
        private void EvaluateStrategies(DateTime currentTime)
        {
            _lastEvaluationTime = DateTime.Now;

            List<SignalRow> newSignals;

            lock (_lock)
            {
                if (_atmStrike <= 0 || _optionStates.Count == 0)
                    return;

                // Get dynamic ATM from MarketAnalyzerLogic (calculated by Option Chain based on straddle prices)
                // This ensures we use the same ATM as Option Chain window shows
                double dynamicAtm = (double)MarketAnalyzerLogic.Instance.GetATMStrike(_underlying);
                double atmToUse = dynamicAtm > 0 ? dynamicAtm : _atmStrike;

                // Log ATM change if significant
                if (dynamicAtm > 0 && Math.Abs(dynamicAtm - _atmStrike) > _strikeStep)
                {
                    _log.Info($"[SignalsOrchestrator] Using dynamic ATM={dynamicAtm} (initial ATM was {_atmStrike})");
                }

                newSignals = new List<SignalRow>();

                foreach (var strategy in _strategies.Where(s => s.IsEnabled))
                {
                    try
                    {
                        var strategySignals = strategy.Evaluate(
                            _optionStates,
                            _barHistories,
                            atmToUse,
                            _strikeStep,
                            currentTime,
                            _signals.ToList());

                        newSignals.AddRange(strategySignals);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[SignalsOrchestrator] Strategy {strategy.Name} error: {ex.Message}");
                    }
                }
            }

            // Add signals on UI thread
            if (newSignals.Count > 0)
            {
                // Determine if signals are real-time (signal time within 5 seconds of system time)
                DateTime systemTime = DateTime.Now;
                const int REALTIME_THRESHOLD_SECONDS = 5;

                _dispatcher.InvokeAsync(() =>
                {
                    foreach (var signal in newSignals)
                    {
                        // Check if this is a real-time signal
                        bool isRealtime = Math.Abs((signal.SignalTime - systemTime).TotalSeconds) <= REALTIME_THRESHOLD_SECONDS;
                        signal.IsRealtime = isRealtime;

                        _signals.Insert(0, signal); // Add new signals at top

                        if (isRealtime)
                        {
                            _log.Info($"[SignalsOrchestrator] REALTIME signal: {signal.StrategyName} {signal.Direction} {signal.Symbol} @ {signal.EntryPrice:F2} - {signal.SignalReason}");
                            TerminalService.Instance.Signal($"[REALTIME] {signal.DirectionStr} {signal.Symbol} @ {signal.EntryPrice:F2} | {signal.SignalReason}");

                            // Trigger Stoxo bridge execution for real-time signals
                            ExecuteSignalViaBridge(signal);
                            signal.ExecutionTriggered = true;
                        }
                        else
                        {
                            _log.Info($"[SignalsOrchestrator] Historical signal: {signal.StrategyName} {signal.Direction} {signal.Symbol} @ {signal.EntryPrice:F2} - {signal.SignalReason} (Time: {signal.SignalTime:HH:mm:ss})");
                            TerminalService.Instance.Signal($"[HIST {signal.SignalTime:HH:mm:ss}] {signal.DirectionStr} {signal.Symbol} @ {signal.EntryPrice:F2} | {signal.SignalReason}");
                        }
                    }

                    // Notify subscribers that stats have changed (new signals added)
                    SignalStatsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        }

        /// <summary>
        /// Executes a signal via the Stoxo SignalBridge service (IB_MappedOrderMod).
        /// This is called for real-time signals to place orders via the execution platform.
        /// </summary>
        private async void ExecuteSignalViaBridge(SignalRow signal)
        {
            try
            {
                // Generate unique order ID for this signal
                signal.BridgeOrderId = SignalBridgeService.Instance.GenerateOrderId();
                _log.Info($"[SignalsOrchestrator] Executing signal via bridge: OrderId={signal.BridgeOrderId} {signal.Symbol}");
                TerminalService.Instance.Info($"[Bridge] Placing order #{signal.BridgeOrderId}: {signal.DirectionStr} {signal.Symbol} Qty={signal.Quantity}");

                // Call bridge to place entry order
                var response = await SignalBridgeService.Instance.PlaceEntryOrderAsync(signal);

                // Update signal with bridge response
                signal.BridgeRequestId = response.RequestId;
                signal.BridgeError = response.ErrorMessage;

                if (response.IsSuccess)
                {
                    _log.Info($"[SignalsOrchestrator] Bridge order placed successfully: RequestId={response.RequestId}");
                    TerminalService.Instance.Signal($"[Bridge] Order #{signal.BridgeOrderId} PLACED - RequestId={response.RequestId}");
                }
                else
                {
                    _log.Warn($"[SignalsOrchestrator] Bridge order failed: {response.ErrorMessage}");
                    TerminalService.Instance.Error($"[Bridge] Order #{signal.BridgeOrderId} FAILED: {response.ErrorMessage}");

                    // Get detailed error from bridge if available
                    if (response.RequestId > 0 && response.RequestId < 90000)
                    {
                        string detailedError = await SignalBridgeService.Instance.GetErrorAsync(response.RequestId);
                        TerminalService.Instance.Error($"[Bridge] Error details: {detailedError}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalsOrchestrator] ExecuteSignalViaBridge error: {ex.Message}", ex);
                TerminalService.Instance.Error($"[Bridge] Exception executing signal: {ex.Message}");
                signal.BridgeError = ex.Message;
            }
        }

        /// <summary>
        /// Places an exit order for an active signal via the Stoxo SignalBridge.
        /// </summary>
        public async void ExitSignalViaBridge(SignalRow signal, double exitPrice = 0, string orderType = "MARKET")
        {
            if (signal.Status != SignalStatus.Active)
            {
                TerminalService.Instance.Warn($"[Bridge] Cannot exit non-active signal: {signal.Symbol} Status={signal.Status}");
                return;
            }

            try
            {
                _log.Info($"[SignalsOrchestrator] Exiting signal via bridge: OrderId={signal.BridgeOrderId} {signal.Symbol}");
                TerminalService.Instance.Info($"[Bridge] Placing exit for #{signal.BridgeOrderId}: {signal.Symbol} @ {(exitPrice > 0 ? exitPrice.ToString("F2") : "MKT")}");

                var response = await SignalBridgeService.Instance.PlaceExitOrderAsync(signal, exitPrice, orderType);

                if (response.IsSuccess)
                {
                    _log.Info($"[SignalsOrchestrator] Bridge exit order placed: RequestId={response.RequestId}");
                    TerminalService.Instance.Signal($"[Bridge] Exit #{signal.BridgeOrderId} PLACED - RequestId={response.RequestId}");

                    // Update signal status
                    signal.Status = SignalStatus.Closed;
                    signal.ExitPrice = exitPrice > 0 ? exitPrice : signal.CurrentPrice;
                    signal.ExitTime = DateTime.Now;
                }
                else
                {
                    _log.Warn($"[SignalsOrchestrator] Bridge exit order failed: {response.ErrorMessage}");
                    TerminalService.Instance.Error($"[Bridge] Exit #{signal.BridgeOrderId} FAILED: {response.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SignalsOrchestrator] ExitSignalViaBridge error: {ex.Message}", ex);
                TerminalService.Instance.Error($"[Bridge] Exception exiting signal: {ex.Message}");
            }
        }

        /// <summary>
        /// Activates a pending signal (simulates order fill).
        /// </summary>
        public void ActivateSignal(string signalId, double entryPrice)
        {
            _dispatcher.InvokeAsync(() =>
            {
                var signal = _signals.FirstOrDefault(s => s.SignalId == signalId);
                if (signal != null && signal.Status == SignalStatus.Pending)
                {
                    signal.Status = SignalStatus.Active;
                    signal.EntryPrice = entryPrice;
                    signal.EntryTime = GetCurrentTime();
                    _log.Info($"[SignalsOrchestrator] Signal activated: {signal.Symbol} @ {entryPrice:F2}");
                }
            });
        }

        /// <summary>
        /// Closes an active signal.
        /// </summary>
        public void CloseSignal(string signalId, double exitPrice)
        {
            CloseSignal(signalId, exitPrice, null);
        }

        /// <summary>
        /// Closes an active signal with an optional reason.
        /// </summary>
        public void CloseSignal(string signalId, double exitPrice, string exitReason)
        {
            _dispatcher.InvokeAsync(() =>
            {
                var signal = _signals.FirstOrDefault(s => s.SignalId == signalId);
                if (signal != null && signal.Status == SignalStatus.Active)
                {
                    signal.Status = SignalStatus.Closed;
                    signal.ExitPrice = exitPrice;
                    signal.ExitTime = GetCurrentTime();

                    // Append exit reason to signal reason if provided
                    if (!string.IsNullOrEmpty(exitReason))
                    {
                        signal.SignalReason = $"{signal.SignalReason} | Exit: {exitReason}";
                    }

                    _log.Info($"[SignalsOrchestrator] Signal closed: {signal.Symbol} @ {exitPrice:F2}, P&L: {signal.RealizedPnL:F2}, Reason: {exitReason ?? "Manual/Opposite signal"}");

                    // Notify subscribers that stats have changed (signal closed)
                    SignalStatsChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// Cancels a pending signal.
        /// </summary>
        public void CancelSignal(string signalId)
        {
            _dispatcher.InvokeAsync(() =>
            {
                var signal = _signals.FirstOrDefault(s => s.SignalId == signalId);
                if (signal != null && signal.Status == SignalStatus.Pending)
                {
                    signal.Status = SignalStatus.Cancelled;
                    _log.Info($"[SignalsOrchestrator] Signal cancelled: {signal.Symbol}");
                }
            });
        }

        /// <summary>
        /// Clears all option states (call when underlying/expiry changes).
        /// </summary>
        public void ClearStates()
        {
            lock (_lock)
            {
                _optionStates.Clear();
            }
        }

        /// <summary>
        /// Gets total unrealized P&L across all active signals.
        /// </summary>
        public double TotalUnrealizedPnL => _signals.Where(s => s.Status == SignalStatus.Active).Sum(s => s.UnrealizedPnL);

        /// <summary>
        /// Gets total realized P&L across all closed signals.
        /// </summary>
        public double TotalRealizedPnL => _signals.Where(s => s.Status == SignalStatus.Closed).Sum(s => s.RealizedPnL);

        /// <summary>
        /// Gets count of active signals.
        /// </summary>
        public int ActiveSignalCount => _signals.Count(s => s.Status == SignalStatus.Active);

        public void Dispose()
        {
            lock (_lock)
            {
                _optionStates.Clear();
            }
        }
    }
}
