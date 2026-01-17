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
    /// Option Prices → Batched Stream (backpressure) → UI Updates
    /// VWAP Updates → Per-symbol streams → Option Chain display
    /// </summary>
    public class MarketDataReactiveHub : IDisposable
    {
        private static readonly Lazy<MarketDataReactiveHub> _instance =
            new Lazy<MarketDataReactiveHub>(() => new MarketDataReactiveHub());
        public static MarketDataReactiveHub Instance => _instance.Value;

        // ═══════════════════════════════════════════════════════════════════
        // INDEX STREAMS
        // ═══════════════════════════════════════════════════════════════════

        // Core Subjects - ReplaySubject(1) ensures late subscribers get the latest value
        private readonly ReplaySubject<IndexPriceUpdate> _giftNiftySubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _niftySubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _sensexSubject = new ReplaySubject<IndexPriceUpdate>(1);
        private readonly ReplaySubject<IndexPriceUpdate> _niftyFuturesSubject = new ReplaySubject<IndexPriceUpdate>(1);

        // BehaviorSubject for projected opens - starts with empty state
        private readonly BehaviorSubject<ProjectedOpenState> _projectedOpenSubject =
            new BehaviorSubject<ProjectedOpenState>(ProjectedOpenState.Empty);

        // ReplaySubject for options generated events - buffers last event so late subscribers get it
        // This is critical: Option Signals waits for Market Analyzer before subscribing, so it needs replay
        private readonly ReplaySubject<OptionsGeneratedEvent> _optionsGeneratedSubject = new ReplaySubject<OptionsGeneratedEvent>(1);

        // ═══════════════════════════════════════════════════════════════════
        // OPTION PRICE STREAMS (with backpressure)
        // ═══════════════════════════════════════════════════════════════════

        // Raw option price subject - all individual option price updates flow through here
        private readonly Subject<OptionPriceUpdate> _optionPriceSubject = new Subject<OptionPriceUpdate>();

        // Option status updates (Pending → Cached → Done)
        private readonly Subject<OptionStatusUpdate> _optionStatusSubject = new Subject<OptionStatusUpdate>();

        // Symbol resolution events (generatedSymbol → zerodhaSymbol)
        private readonly Subject<(string generated, string zerodha)> _symbolResolvedSubject = new Subject<(string, string)>();

        // ═══════════════════════════════════════════════════════════════════
        // VWAP & STRADDLE STREAMS
        // ═══════════════════════════════════════════════════════════════════

        // VWAP updates for individual symbols
        private readonly Subject<VWAPUpdate> _vwapSubject = new Subject<VWAPUpdate>();

        // Synthetic straddle price updates
        private readonly Subject<StraddlePriceUpdate> _straddlePriceSubject = new Subject<StraddlePriceUpdate>();

        // ═══════════════════════════════════════════════════════════════════
        // TBS PRICE SYNC STREAM (ReplaySubject for late subscribers)
        // ═══════════════════════════════════════════════════════════════════

        // Price sync ready signal - ReplaySubject(1) ensures late subscribers get the value
        private readonly ReplaySubject<bool> _priceSyncReadySubject = new ReplaySubject<bool>(1);
        private bool _priceSyncPublished = false;

        // ═══════════════════════════════════════════════════════════════════
        // TOKEN & INITIALIZATION STREAMS
        // ═══════════════════════════════════════════════════════════════════

        // Token validation ready signal - ReplaySubject(1) ensures late subscribers get the value
        // This is the Rx-based equivalent of Connector.WhenTokenReady
        private readonly ReplaySubject<bool> _tokenReadySubject = new ReplaySubject<bool>(1);
        private bool _tokenReadyPublished = false;

        // Instrument database ready signal - fires after token is valid AND db is refreshed
        private readonly ReplaySubject<bool> _instrumentDbReadySubject = new ReplaySubject<bool>(1);
        private bool _instrumentDbReadyPublished = false;

        // Detailed initialization state stream - BehaviorSubject so late subscribers get current state
        private readonly BehaviorSubject<InitializationState> _initializationStateSubject =
            new BehaviorSubject<InitializationState>(InitializationState.NotStarted);

        // ═══════════════════════════════════════════════════════════════════
        // BACKPRESSURE CONFIGURATION
        // ═══════════════════════════════════════════════════════════════════

        // Batch size and time window for option price updates
        private const int OPTION_BATCH_SIZE = 50;
        private const int OPTION_BATCH_INTERVAL_MS = 100;

        // Subscription management
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();
        private bool _disposed = false;

        // State tracking
        private bool _projectedOpensCalculated = false;
        private readonly object _syncLock = new object();
        private readonly object _publishLock = new object();

        private void SafeOnNext<T>(IObserver<T> observer, T value)
        {
            lock (_publishLock)
            {
                observer.OnNext(value);
            }
        }

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

        #region Option Price Streams (Read-Only)

        /// <summary>
        /// Raw stream of individual option price updates.
        /// Use OptionPriceBatchStream for UI updates with backpressure.
        /// </summary>
        public IObservable<OptionPriceUpdate> OptionPriceStream => _optionPriceSubject.AsObservable();

        /// <summary>
        /// Batched option price updates with backpressure.
        /// Batches by time (100ms) OR count (50), whichever comes first.
        /// This prevents UI thread flooding when 122 options update rapidly.
        /// </summary>
        public IObservable<IList<OptionPriceUpdate>> OptionPriceBatchStream =>
            _optionPriceSubject
                .Buffer(TimeSpan.FromMilliseconds(OPTION_BATCH_INTERVAL_MS), OPTION_BATCH_SIZE)
                .Where(batch => batch.Count > 0);

        /// <summary>
        /// Sampled option price stream - takes latest price per symbol every 100ms.
        /// Use this for per-symbol throttling.
        /// </summary>
        public IObservable<OptionPriceUpdate> OptionPriceSampledStream =>
            _optionPriceSubject
                .GroupBy(o => o.Symbol)
                .SelectMany(group => group.Sample(TimeSpan.FromMilliseconds(100)));

        /// <summary>
        /// Stream of option status updates (Pending → Cached → Done).
        /// </summary>
        public IObservable<OptionStatusUpdate> OptionStatusStream => _optionStatusSubject.AsObservable();

        /// <summary>
        /// Stream of symbol resolution events (generatedSymbol → zerodhaSymbol).
        /// </summary>
        public IObservable<(string generated, string zerodha)> SymbolResolvedStream => _symbolResolvedSubject.AsObservable();

        /// <summary>
        /// Stream of VWAP updates for all symbols.
        /// </summary>
        public IObservable<VWAPUpdate> VWAPStream => _vwapSubject.AsObservable();

        /// <summary>
        /// Gets a filtered VWAP stream for a specific symbol.
        /// </summary>
        public IObservable<VWAPUpdate> GetVWAPStreamForSymbol(string symbol) =>
            VWAPStream.Where(v => v.Symbol == symbol);

        /// <summary>
        /// Stream of synthetic straddle price updates.
        /// </summary>
        public IObservable<StraddlePriceUpdate> StraddlePriceStream => _straddlePriceSubject.AsObservable();

        /// <summary>
        /// Stream that emits true when price sync is ready (NIFTY and SENSEX prices received).
        /// Uses ReplaySubject(1) so late subscribers (like TBS Manager opened after startup)
        /// immediately receive the value if it was already published.
        /// </summary>
        public IObservable<bool> PriceSyncReadyStream => _priceSyncReadySubject.AsObservable();

        /// <summary>
        /// Stream that emits true when Zerodha token validation is complete.
        /// Uses ReplaySubject(1) so late subscribers immediately receive the value.
        /// Subscribe to this before making any API calls that require authentication.
        /// </summary>
        public IObservable<bool> TokenReadyStream => _tokenReadySubject.AsObservable();

        /// <summary>
        /// Stream that emits true when instrument database is ready (token valid + db refreshed).
        /// Uses ReplaySubject(1) so late subscribers immediately receive the value.
        /// Subscribe to this before performing instrument lookups.
        /// </summary>
        public IObservable<bool> InstrumentDbReadyStream => _instrumentDbReadySubject.AsObservable();

        /// <summary>
        /// Detailed initialization state stream. Uses BehaviorSubject so late subscribers
        /// immediately receive the current state. Subscribe to track initialization progress
        /// with retry counts, phase changes, and error messages.
        /// </summary>
        public IObservable<InitializationState> InitializationStateStream => _initializationStateSubject.AsObservable();

        /// <summary>
        /// Convenience stream that emits once when initialization reaches Ready state.
        /// Use this to await full initialization before starting subscriptions.
        /// </summary>
        public IObservable<InitializationState> WhenReady => _initializationStateSubject
            .Where(s => s.IsReady)
            .Take(1);

        /// <summary>
        /// Gets the current initialization state synchronously.
        /// </summary>
        public InitializationState CurrentInitializationState => _initializationStateSubject.Value;

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
                SafeOnNext(_projectedOpenSubject, state);

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
                SafeOnNext(subject, update);
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

            SafeOnNext(subject, update);
            Logger.Debug($"[MarketDataReactiveHub] Published close: {symbol} = {close:F2}");
        }

        /// <summary>
        /// Publishes options generated event.
        /// </summary>
        public void PublishOptionsGenerated(OptionsGeneratedEvent evt)
        {
            if (evt == null || evt.Options == null) return;

            SafeOnNext(_optionsGeneratedSubject, evt);
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

        #region Option Price & Status Publish Methods

        /// <summary>
        /// Publishes an option price update.
        /// </summary>
        /// <param name="symbol">Option symbol</param>
        /// <param name="price">Current price</param>
        /// <param name="volume">Trading volume (optional)</param>
        /// <param name="source">Source of update (WebSocket, Historical, Simulated)</param>
        /// <param name="timestamp">Optional timestamp - uses DateTime.Now if not provided (for simulation replay)</param>
        public void PublishOptionPrice(string symbol, double price, double volume = 0, string source = "WebSocket", DateTime? timestamp = null)
        {
            if (string.IsNullOrEmpty(symbol) || price <= 0) return;

            var update = new OptionPriceUpdate
            {
                Symbol = symbol,
                Price = price,
                Volume = volume,
                Timestamp = timestamp ?? DateTime.Now,
                Source = source
            };

            SafeOnNext(_optionPriceSubject, update);
        }

        /// <summary>
        /// Publishes an option status update.
        /// </summary>
        /// <param name="symbol">Option symbol</param>
        /// <param name="status">Status message</param>
        public void PublishOptionStatus(string symbol, string status)
        {
            if (string.IsNullOrEmpty(symbol)) return;

            var update = new OptionStatusUpdate
            {
                Symbol = symbol,
                Status = status,
                Timestamp = DateTime.Now
            };

            SafeOnNext(_optionStatusSubject, update);
        }

        /// <summary>
        /// Publishes a symbol resolution event.
        /// </summary>
        /// <param name="generatedSymbol">Generated symbol (e.g., NIFTY_25JAN_23500_CE)</param>
        /// <param name="zerodhaSymbol">Zerodha trading symbol (e.g., NIFTY25JAN23500CE)</param>
        public void PublishSymbolResolved(string generatedSymbol, string zerodhaSymbol)
        {
            if (string.IsNullOrEmpty(generatedSymbol) || string.IsNullOrEmpty(zerodhaSymbol)) return;

            SafeOnNext(_symbolResolvedSubject, (generatedSymbol, zerodhaSymbol));
            Logger.Debug($"[MarketDataReactiveHub] Symbol resolved: {generatedSymbol} → {zerodhaSymbol}");
        }

        #endregion

        #region VWAP & Straddle Publish Methods

        /// <summary>
        /// Publishes a VWAP update for a symbol.
        /// </summary>
        public void PublishVWAP(string symbol, double vwap, double sd1Upper, double sd1Lower, double sd2Upper, double sd2Lower)
        {
            if (string.IsNullOrEmpty(symbol)) return;

            var update = new VWAPUpdate
            {
                Symbol = symbol,
                VWAP = vwap,
                SD1Upper = sd1Upper,
                SD1Lower = sd1Lower,
                SD2Upper = sd2Upper,
                SD2Lower = sd2Lower,
                Timestamp = DateTime.Now
            };

            SafeOnNext(_vwapSubject, update);
        }

        /// <summary>
        /// Publishes a VWAP update from a VWAPData object.
        /// </summary>
        public void PublishVWAP(VWAPData data)
        {
            if (data == null || string.IsNullOrEmpty(data.Symbol)) return;

            PublishVWAP(data.Symbol, data.VWAP, data.SD1Upper, data.SD1Lower, data.SD2Upper, data.SD2Lower);
        }

        /// <summary>
        /// Publishes a synthetic straddle price update.
        /// </summary>
        public void PublishStraddlePrice(string symbol, double price, double cePrice, double pePrice, double strike = 0)
        {
            if (string.IsNullOrEmpty(symbol)) return;

            var update = new StraddlePriceUpdate
            {
                Symbol = symbol,
                Price = price,
                CEPrice = cePrice,
                PEPrice = pePrice,
                Strike = strike,
                Timestamp = DateTime.Now
            };

            SafeOnNext(_straddlePriceSubject, update);
        }

        /// <summary>
        /// Publishes price sync ready signal. Uses ReplaySubject(1) so late subscribers
        /// (like TBS Manager opened after Option Chain is ready) will immediately receive the value.
        /// This method is idempotent - subsequent calls are ignored.
        /// </summary>
        public void PublishPriceSyncReady()
        {
            lock (_syncLock)
            {
                if (_priceSyncPublished) return;
                _priceSyncPublished = true;
            }

            Logger.Info("[MarketDataReactiveHub] Publishing PriceSyncReady to Rx stream");
            SafeOnNext(_priceSyncReadySubject, true);
        }

        /// <summary>
        /// Publishes token ready signal. Called by Connector after token validation completes.
        /// Uses ReplaySubject(1) so late subscribers immediately receive the value.
        /// This method is idempotent - subsequent calls are ignored.
        /// </summary>
        /// <param name="isValid">True if token is valid, false otherwise</param>
        public void PublishTokenReady(bool isValid)
        {
            lock (_syncLock)
            {
                if (_tokenReadyPublished) return;
                _tokenReadyPublished = true;
            }

            Logger.Info($"[MarketDataReactiveHub] Publishing TokenReady to Rx stream: isValid={isValid}");
            SafeOnNext(_tokenReadySubject, isValid);
        }

        /// <summary>
        /// Publishes instrument database ready signal. Called after db refresh completes.
        /// Uses ReplaySubject(1) so late subscribers immediately receive the value.
        /// This method is idempotent - subsequent calls are ignored.
        /// </summary>
        /// <param name="isReady">True if database is ready, false otherwise</param>
        public void PublishInstrumentDbReady(bool isReady)
        {
            lock (_syncLock)
            {
                if (_instrumentDbReadyPublished) return;
                _instrumentDbReadyPublished = true;
            }

            Logger.Info($"[MarketDataReactiveHub] Publishing InstrumentDbReady to Rx stream: isReady={isReady}");
            SafeOnNext(_instrumentDbReadySubject, isReady);
        }

        /// <summary>
        /// Publishes a detailed initialization state update.
        /// Called at various stages during initialization to provide progress tracking.
        /// </summary>
        /// <param name="state">The current initialization state</param>
        public void PublishInitializationState(InitializationState state)
        {
            if (state == null) return;

            Logger.Info($"[MarketDataReactiveHub] InitializationState: {state}");
            SafeOnNext(_initializationStateSubject, state);

            // Also publish to simple streams for backward compatibility
            if (state.Phase == InitializationPhase.TokenValidated)
            {
                PublishTokenReady(state.IsTokenValid);
            }
            else if (state.Phase == InitializationPhase.Ready || state.Phase == InitializationPhase.Failed)
            {
                PublishInstrumentDbReady(state.IsInstrumentDbReady);
            }
        }

        /// <summary>
        /// Helper to publish initialization state for a specific phase.
        /// </summary>
        public void PublishInitializationPhase(InitializationPhase phase, string message, int progressPercent = 0)
        {
            PublishInitializationState(InitializationState.ForPhase(phase, message, progressPercent));
        }

        /// <summary>
        /// Helper to publish initialization state with retry information.
        /// </summary>
        public void PublishInitializationRetry(int attempt, int maxRetries, string message)
        {
            var state = InitializationState.ForPhase(
                InitializationPhase.DownloadingInstruments,
                message,
                (attempt * 100) / maxRetries);
            state.RetryAttempt = attempt;
            state.MaxRetries = maxRetries;
            PublishInitializationState(state);
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
        // NiftyFuturesAliases is mutable (aliases added at runtime) - requires synchronization
        private static readonly object _niftyFuturesAliasesLock = new object();
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

            // Thread-safe check for mutable NiftyFuturesAliases
            bool isNiftyFuturesAlias;
            lock (_niftyFuturesAliasesLock)
            {
                isNiftyFuturesAlias = NiftyFuturesAliases.Contains(symbol);
            }
            if (isNiftyFuturesAlias || IsNiftyFuturesSymbol(symbol))
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

            // Thread-safe check for mutable NiftyFuturesAliases
            bool isNiftyFuturesAlias;
            lock (_niftyFuturesAliasesLock)
            {
                isNiftyFuturesAlias = NiftyFuturesAliases.Contains(symbol);
            }
            if (isNiftyFuturesAlias || IsNiftyFuturesSymbol(symbol))
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
        /// Thread-safe operation.
        /// </summary>
        public void AddNiftyFuturesAlias(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return;

            lock (_niftyFuturesAliasesLock)
            {
                if (!NiftyFuturesAliases.Contains(alias))
                {
                    NiftyFuturesAliases.Add(alias);
                    Logger.Debug($"[MarketDataReactiveHub] Added NIFTY_I alias: {alias}");
                }
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
                SafeOnNext(_projectedOpenSubject, ProjectedOpenState.Empty);
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

            // Index streams
            _giftNiftySubject.Dispose();
            _niftySubject.Dispose();
            _sensexSubject.Dispose();
            _niftyFuturesSubject.Dispose();
            _projectedOpenSubject.Dispose();
            _optionsGeneratedSubject.Dispose();

            // Option price streams
            _optionPriceSubject.Dispose();
            _optionStatusSubject.Dispose();
            _symbolResolvedSubject.Dispose();

            // VWAP & Straddle streams
            _vwapSubject.Dispose();
            _straddlePriceSubject.Dispose();

            // TBS price sync stream
            _priceSyncReadySubject.Dispose();

            // Token & initialization streams
            _tokenReadySubject.Dispose();
            _instrumentDbReadySubject.Dispose();
            _initializationStateSubject.Dispose();

            Logger.Info("[MarketDataReactiveHub] Disposed");
        }

        #endregion
    }
}
