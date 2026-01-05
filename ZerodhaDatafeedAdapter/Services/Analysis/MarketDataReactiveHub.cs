using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Single source of truth for all reactive market data streams.
    /// Centralizes the fragmented event-based communication into unified Rx streams.
    ///
    /// Data Flow:
    /// GIFT_NIFTY (with retry) → Wait for SENSEX/NIFTY prior closes → Projected Opens → Generate Options
    /// </summary>
    public class MarketDataReactiveHub : IDisposable
    {
        private static readonly Lazy<MarketDataReactiveHub> _instance =
            new Lazy<MarketDataReactiveHub>(() => new MarketDataReactiveHub());
        public static MarketDataReactiveHub Instance => _instance.Value;

        // Core Subjects - ReplaySubject(1) ensures late subscribers get the latest value
        private readonly ReplaySubject<IndexPriceUpdate> _giftNiftySubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _niftySubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _sensexSubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _niftyFuturesSubject = new ReplaySubject<IndexPriceUpdate>(1);

        // BehaviorSubject for projected opens - starts with empty state
        private readonly BehaviorSubject<ProjectedOpenState> _projectedOpenSubject =
            new BehaviorSubject<ProjectedOpenState>(ProjectedOpenState.Empty);

        // Subject for options generated events
        private readonly Subject<OptionsGeneratedEvent> _optionsGeneratedSubject = new Subject<OptionsGeneratedEvent>();

        // Subscription management
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();
        private bool _disposed = false;

        // State tracking
        private bool _projectedOpensCalculated = false;
        private readonly object _syncLock = new object();

        #region Public Observable Streams (Read-Only)

        /// <summary>
        /// Stream of GIFT NIFTY price updates. Late subscribers receive the last emitted value.
        /// </summary>
        public IObservable<IndexPriceUpdate> GiftNiftyStream => _giftNiftySubject.AsObservable();

        /// <summary>
        /// Stream of NIFTY 50 price updates. Late subscribers receive the last emitted value.
        /// </summary>
        public IObservable<IndexPriceUpdate> NiftyStream => _niftySubject.AsObservable();

        /// <summary>
        /// Stream of SENSEX price updates. Late subscribers receive the last emitted value.
        /// </summary>
        public IObservable<IndexPriceUpdate> SensexStream => _sensexSubject.AsObservable();

        /// <summary>
        /// Stream of NIFTY Futures price updates. Late subscribers receive the last emitted value.
        /// </summary>
        public IObservable<IndexPriceUpdate> NiftyFuturesStream => _niftyFuturesSubject.AsObservable();

        /// <summary>
        /// Stream of projected open calculations. Starts with Empty state, emits Complete state once calculated.
        /// </summary>
        public IObservable<ProjectedOpenState> ProjectedOpenStream => _projectedOpenSubject.AsObservable();

        /// <summary>
        /// Stream of options generated events. Fires once per session when options are generated.
        /// </summary>
        public IObservable<OptionsGeneratedEvent> OptionsGeneratedStream => _optionsGeneratedSubject.AsObservable();

        /// <summary>
        /// Combined stream of all index price updates.
        /// </summary>
        public IObservable<IndexPriceUpdate> AllIndicesStream =>
            GiftNiftyStream.Merge(NiftyStream).Merge(SensexStream).Merge(NiftyFuturesStream);

        #endregion

        #region Constructor

        private MarketDataReactiveHub()
        {
            Logger.Info("[MarketDataReactiveHub] Initializing singleton instance");
            SetupProjectedOpensPipeline();
            Logger.Info("[MarketDataReactiveHub] Initialized - reactive pipelines ready");
        }

        #endregion

        #region Projected Opens Pipeline

        /// <summary>
        /// Sets up the CombineLatest pipeline for projected opens calculation.
        /// Fires ONCE when GIFT NIFTY change% + NIFTY/SENSEX prior closes are available.
        /// </summary>
        private void SetupProjectedOpensPipeline()
        {
            // GIFT NIFTY change% - need valid change (close must be set)
            var giftChangeStream = _giftNiftySubject
                .Where(g => g.HasClose && g.NetChangePercent != 0)
                .Select(g => g.NetChangePercent)
                .DistinctUntilChanged();

            // NIFTY prior close - take first valid value
            var niftyCloseStream = _niftySubject
                .Where(n => n.HasClose)
                .Select(n => n.Close)
                .Take(1);

            // SENSEX prior close - take first valid value
            var sensexCloseStream = _sensexSubject
                .Where(s => s.HasClose)
                .Select(s => s.Close)
                .Take(1);

            // CombineLatest - fires when all three streams have emitted
            var projectedOpensPipeline = giftChangeStream
                .CombineLatest(niftyCloseStream, sensexCloseStream,
                    (giftChg, niftyClose, sensexClose) => new
                    {
                        GiftChangePercent = giftChg,
                        NiftyClose = niftyClose,
                        SensexClose = sensexClose
                    })
                .Take(1) // Calculate only once
                .Subscribe(
                    data => CalculateAndPublishProjectedOpens(data.GiftChangePercent, data.NiftyClose, data.SensexClose),
                    ex => Logger.Error($"[MarketDataReactiveHub] Projected opens pipeline error: {ex.Message}", ex));

            _subscriptions.Add(projectedOpensPipeline);
            Logger.Info("[MarketDataReactiveHub] Projected opens pipeline configured");
        }

        private void CalculateAndPublishProjectedOpens(double giftChangePercent, double niftyClose, double sensexClose)
        {
            lock (_syncLock)
            {
                if (_projectedOpensCalculated)
                {
                    Logger.Debug("[MarketDataReactiveHub] Projected opens already calculated, skipping");
                    return;
                }

                // giftChangePercent is in percentage form (e.g., 0.09 means 0.09%)
                double giftChgDecimal = giftChangePercent / 100.0;

                double niftyProjOpen = niftyClose * (1 + giftChgDecimal);
                double sensexProjOpen = sensexClose * (1 + giftChgDecimal);

                var state = new ProjectedOpenState
                {
                    NiftyProjectedOpen = niftyProjOpen,
                    SensexProjectedOpen = sensexProjOpen,
                    GiftChangePercent = giftChangePercent,
                    IsComplete = true,
                    CalculatedAt = DateTime.Now
                };

                _projectedOpensCalculated = true;
                _projectedOpenSubject.OnNext(state);

                Logger.Info($"[MarketDataReactiveHub] Projected Opens: GIFT Chg={giftChangePercent:+0.00;-0.00}%, NIFTY={niftyProjOpen:F0}, SENSEX={sensexProjOpen:F0}");
            }
        }

        #endregion

        #region Publish Methods

        /// <summary>
        /// Publishes a price update for an index instrument.
        /// Automatically routes to the correct subject based on symbol.
        /// </summary>
        /// <param name="symbol">Symbol name (can be any alias like GIFT_NIFTY, NIFTY 50, etc.)</param>
        /// <param name="price">Current/Last traded price</param>
        /// <param name="close">Prior day's closing price (0 if not available)</param>
        public void PublishIndexPrice(string symbol, double price, double close = 0)
        {
            if (string.IsNullOrEmpty(symbol)) return;

            var update = new IndexPriceUpdate
            {
                Symbol = NormalizeSymbol(symbol),
                Price = price,
                Close = close,
                NetChangePercent = close > 0 ? ((price - close) / close) * 100.0 : 0,
                Timestamp = DateTime.Now
            };

            // Route to correct subject
            var subject = GetSubjectForSymbol(symbol);
            if (subject != null)
            {
                subject.OnNext(update);
                Logger.Debug($"[MarketDataReactiveHub] Published: {update}");
            }
            else
            {
                Logger.Warn($"[MarketDataReactiveHub] Unknown symbol: {symbol}");
            }
        }

        /// <summary>
        /// Updates only the close price for an index (used when historical data arrives separately).
        /// </summary>
        public void PublishIndexClose(string symbol, double close)
        {
            if (string.IsNullOrEmpty(symbol) || close <= 0) return;

            var subject = GetSubjectForSymbol(symbol);
            if (subject == null)
            {
                Logger.Warn($"[MarketDataReactiveHub] PublishIndexClose: Unknown symbol: {symbol}");
                return;
            }

            // Get current state and update close
            IndexPriceUpdate current = null;
            var sub = subject.Take(1).Subscribe(u => current = u);
            sub.Dispose();

            var update = new IndexPriceUpdate
            {
                Symbol = NormalizeSymbol(symbol),
                Price = current?.Price ?? 0,
                Close = close,
                NetChangePercent = current?.Price > 0 && close > 0
                    ? ((current.Price - close) / close) * 100.0
                    : 0,
                Timestamp = DateTime.Now
            };

            subject.OnNext(update);
            Logger.Debug($"[MarketDataReactiveHub] Published close: {symbol} = {close:F2}");
        }

        /// <summary>
        /// Publishes options generated event.
        /// </summary>
        public void PublishOptionsGenerated(OptionsGeneratedEvent evt)
        {
            if (evt == null || evt.Options == null) return;

            _optionsGeneratedSubject.OnNext(evt);
            Logger.Info($"[MarketDataReactiveHub] Published OptionsGenerated: {evt}");
        }

        /// <summary>
        /// Publishes options generated event from a list of mapped instruments.
        /// </summary>
        public void PublishOptionsGenerated(List<MappedInstrument> options, string underlying, DateTime expiry, int dte, double atmStrike, double projectedOpen)
        {
            var evt = new OptionsGeneratedEvent
            {
                Options = options,
                SelectedUnderlying = underlying,
                SelectedExpiry = expiry,
                DTE = dte,
                ATMStrike = atmStrike,
                ProjectedOpenUsed = projectedOpen,
                GeneratedAt = DateTime.Now
            };

            PublishOptionsGenerated(evt);
        }

        #endregion

        #region Symbol Routing

        // Symbol normalization sets (all aliases that map to each index)
        private static readonly HashSet<string> GiftNiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GIFT_NIFTY", "GIFT NIFTY", "GIFTNIFTY" };
        private static readonly HashSet<string> NiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "NIFTY", "NIFTY 50", "NIFTY_50", "NIFTY50", "NIFTY_SPOT" };
        private static readonly HashSet<string> SensexAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SENSEX", "SENSEX_SPOT", "BSE:SENSEX" };
        private static readonly HashSet<string> NiftyFuturesAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "NIFTY_I" };

        private ReplaySubject<IndexPriceUpdate> GetSubjectForSymbol(string symbol)
        {
            if (GiftNiftyAliases.Contains(symbol))
                return _giftNiftySubject;
            if (NiftyAliases.Contains(symbol))
                return _niftySubject;
            if (SensexAliases.Contains(symbol))
                return _sensexSubject;
            if (NiftyFuturesAliases.Contains(symbol) || IsNiftyFuturesSymbol(symbol))
                return _niftyFuturesSubject;

            return null;
        }

        private string NormalizeSymbol(string symbol)
        {
            if (GiftNiftyAliases.Contains(symbol))
                return "GIFT NIFTY";
            if (NiftyAliases.Contains(symbol))
                return "NIFTY 50";
            if (SensexAliases.Contains(symbol))
                return "SENSEX";
            if (NiftyFuturesAliases.Contains(symbol) || IsNiftyFuturesSymbol(symbol))
                return "NIFTY_I";

            return symbol;
        }

        private bool IsNiftyFuturesSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            // Match pattern: starts with NIFTY, ends with FUT (e.g., NIFTY26JANFUT)
            return symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) &&
                   symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds a dynamic alias for NIFTY Futures (e.g., NIFTY26JANFUT resolved at runtime).
        /// </summary>
        public void AddNiftyFuturesAlias(string alias)
        {
            if (!string.IsNullOrEmpty(alias) && !NiftyFuturesAliases.Contains(alias))
            {
                NiftyFuturesAliases.Add(alias);
                Logger.Debug($"[MarketDataReactiveHub] Added NIFTY_I alias: {alias}");
            }
        }

        #endregion

        #region Retry Helpers

        /// <summary>
        /// Creates an observable that retries the source with exponential backoff.
        /// </summary>
        /// <typeparam name="T">Result type</typeparam>
        /// <param name="source">Source observable</param>
        /// <param name="maxRetries">Maximum number of retries</param>
        /// <param name="initialDelayMs">Initial delay in milliseconds</param>
        /// <param name="operationName">Name for logging</param>
        public static IObservable<T> WithRetry<T>(
            IObservable<T> source,
            int maxRetries = 5,
            int initialDelayMs = 500,
            string operationName = "Operation")
        {
            return source.RetryWhen(errors => errors
                .Zip(Observable.Range(1, maxRetries + 1), (error, attempt) => new { error, attempt })
                .SelectMany(x =>
                {
                    if (x.attempt > maxRetries)
                    {
                        Logger.Error($"[MarketDataReactiveHub] {operationName}: Max retries ({maxRetries}) exceeded");
                        return Observable.Throw<long>(x.error);
                    }

                    var delay = TimeSpan.FromMilliseconds(initialDelayMs * Math.Pow(2, x.attempt - 1));
                    Logger.Warn($"[MarketDataReactiveHub] {operationName}: Retry {x.attempt}/{maxRetries} in {delay.TotalMilliseconds}ms - {x.error.Message}");
                    return Observable.Timer(delay);
                }));
        }

        /// <summary>
        /// Creates a deferred observable that retries an async operation with exponential backoff.
        /// </summary>
        public static IObservable<T> DeferWithRetry<T>(
            Func<IObservable<T>> factory,
            int maxRetries = 5,
            int initialDelayMs = 500,
            string operationName = "Operation")
        {
            return WithRetry(Observable.Defer(factory), maxRetries, initialDelayMs, operationName);
        }

        #endregion

        #region State Queries

        /// <summary>
        /// Gets the current projected open state synchronously.
        /// </summary>
        public ProjectedOpenState GetCurrentProjectedOpenState()
        {
            ProjectedOpenState state = null;
            var sub = _projectedOpenSubject.Take(1).Subscribe(s => state = s);
            sub.Dispose();
            return state ?? ProjectedOpenState.Empty;
        }

        /// <summary>
        /// Gets the current price for an index synchronously.
        /// </summary>
        public IndexPriceUpdate GetCurrentPrice(string symbol)
        {
            var subject = GetSubjectForSymbol(symbol);
            if (subject == null) return null;

            IndexPriceUpdate current = null;
            var sub = subject.Take(1).Subscribe(u => current = u);
            sub.Dispose();
            return current;
        }

        /// <summary>
        /// Resets the projected opens calculation flag (for manual regeneration).
        /// </summary>
        public void ResetProjectedOpens()
        {
            lock (_syncLock)
            {
                _projectedOpensCalculated = false;
                _projectedOpenSubject.OnNext(ProjectedOpenState.Empty);
                Logger.Info("[MarketDataReactiveHub] Projected opens reset");
            }

            // Re-setup the pipeline
            SetupProjectedOpensPipeline();
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Logger.Info("[MarketDataReactiveHub] Disposing...");

            _subscriptions.Dispose();

            _giftNiftySubject.Dispose();
            _niftySubject.Dispose();
            _sensexSubject.Dispose();
            _niftyFuturesSubject.Dispose();
            _projectedOpenSubject.Dispose();
            _optionsGeneratedSubject.Dispose();

            Logger.Info("[MarketDataReactiveHub] Disposed");
        }

        #endregion
    }
}
