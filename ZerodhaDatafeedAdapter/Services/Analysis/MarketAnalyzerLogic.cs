using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Hedge;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Telegram;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Core coordinator for Market Analyzer logic.
    /// Manages the ticker list and orchestrates price updates and option generation.
    /// Calculates projected opens based on GIFT NIFTY change and auto-generates options.
    /// </summary>
    public class MarketAnalyzerLogic
    {
        private static readonly Lazy<MarketAnalyzerLogic> _instance = new Lazy<MarketAnalyzerLogic>(() => new MarketAnalyzerLogic());
        public static MarketAnalyzerLogic Instance => _instance.Value;

        // Static ticker instances - always available (symbol names match zerodhaSymbol from index_mappings.json)
        public TickerData GiftNiftyTicker { get; } = new TickerData { Symbol = "GIFT NIFTY" };
        public TickerData NiftyTicker { get; } = new TickerData { Symbol = "NIFTY 50" };
        public TickerData SensexTicker { get; } = new TickerData { Symbol = "SENSEX" };
        public TickerData NiftyFuturesTicker { get; } = new TickerData { Symbol = "NIFTY_I" }; // NIFTY Futures (current month)

        // Observable collection for UI binding (contains the 4 static tickers)
        public ObservableCollection<TickerData> Tickers { get; } = new ObservableCollection<TickerData>();

        // Spot prices - stored locally for calculations
        public double GiftNiftyPrice { get; private set; }
        public double NiftySpotPrice { get; private set; }
        public double SensexSpotPrice { get; private set; }
        public double NiftyFuturesPrice { get; private set; }

        // Prior close for GIFT NIFTY (needed for projected open calculation)
        public double GiftNiftyPriorClose { get; private set; }

        // Projected open prices
        public double ProjectedNiftyOpenPrice { get; private set; }
        public double ProjectedSensexOpenPrice { get; private set; }

        // NIFTY Futures contract info (resolved dynamically at startup)
        public string NiftyFuturesSymbol { get; private set; }
        public long NiftyFuturesToken { get; private set; }
        public DateTime NiftyFuturesExpiry { get; private set; }

        // Symbol normalization sets (all aliases that map to each index)
        private static readonly HashSet<string> GiftNiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GIFT_NIFTY", "GIFT NIFTY", "GIFTNIFTY" };
        private static readonly HashSet<string> NiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "NIFTY", "NIFTY 50", "NIFTY_50", "NIFTY50", "NIFTY_SPOT" };
        private static readonly HashSet<string> SensexAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SENSEX", "SENSEX_SPOT", "BSE:SENSEX" };
        // NIFTY_I aliases - matches any symbol starting with NIFTY and ending with FUT (e.g., NIFTY26JANFUT)
        private HashSet<string> _niftyFuturesAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NIFTY_I" };

        // System.Reactive - Price update subjects for event-driven architecture
        private readonly Subject<TickerPriceUpdate> _priceUpdateSubject = new Subject<TickerPriceUpdate>();
        private readonly Subject<ProjectedOpenUpdate> _projectedOpenSubject = new Subject<ProjectedOpenUpdate>();
        private IDisposable _projectedOpenSubscription;

        // Observable streams for UI binding
        public IObservable<TickerPriceUpdate> PriceUpdateStream => _priceUpdateSubject.AsObservable();
        public IObservable<ProjectedOpenUpdate> ProjectedOpenStream => _projectedOpenSubject.AsObservable();

        // Selection State
        public string SelectedExpiry { get; set; }
        public string SelectedUnderlying { get; set; }
        public bool SelectedIsMonthlyExpiry { get; set; }

        // Events
        public event Action<string> StatusUpdated;
        public event Action<string> TickerUpdated;
        public event Action<string, string> HistoricalDataStatusChanged;
        public event Action<List<MappedInstrument>> OptionsGenerated;
        public event Action PriceSyncReady;
        public event Action<string, double> OptionPriceUpdated;
        public event Action<string, double> PriceUpdated;

        private bool _priceSyncFired = false;
        /// <summary>
        /// Returns true if price sync is ready (NIFTY and SENSEX prices received).
        /// Used by late subscribers to check if they missed the PriceSyncReady event.
        /// </summary>
        public bool IsPriceSyncReady => _priceSyncFired;
        private int _optionsAlreadyGenerated = 0; // 0=false, 1=true - use Interlocked for thread-safe check-and-set
        private readonly object _syncLock = new object();

        // Cached expiries and lot sizes
        private readonly Dictionary<string, List<DateTime>> _cachedExpiries = new Dictionary<string, List<DateTime>>();
        private readonly Dictionary<string, int> _cachedLotSizes = new Dictionary<string, int>();
        private readonly object _expiryLock = new object();
        private readonly object _lotSizeLock = new object();
        // Internal price cache to replace legacy MarketDataHub
        private readonly ConcurrentDictionary<string, double> _priceCache = new ConcurrentDictionary<string, double>();

        private MarketAnalyzerLogic()
        {
            Logger.Info("[MarketAnalyzerLogic] Constructor: Initializing singleton instance");

            // Initialize Tickers collection with static instances
            Tickers.Add(GiftNiftyTicker);
            Tickers.Add(NiftyTicker);
            Tickers.Add(SensexTicker);
            Tickers.Add(NiftyFuturesTicker);

            // Resolve NIFTY Futures contract dynamically when instrument DB is ready
            // Uses Rx to await the InstrumentDbReadyStream - fires once when DB is loaded
            SubscribeToInstrumentDbReadyForFuturesResolution();

            // Setup System.Reactive pipeline for projected opens calculation
            // This fires ONCE when all required data is available (GIFT change% + NIFTY/SENSEX prior close)
            SetupReactiveProjectedOpensPipeline();
        }



        /// <summary>
        /// Subscribes to InstrumentDbReadyStream to resolve NIFTY futures when DB is ready.
        /// This decouples the constructor from blocking on database availability.
        /// Includes 90-second timeout to prevent infinite wait if DB initialization fails.
        /// </summary>
        private void SubscribeToInstrumentDbReadyForFuturesResolution()
        {
            MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                .Where(ready => ready)
                .Timeout(TimeSpan.FromSeconds(90)) // Timeout to prevent infinite wait
                .Take(1) // Only resolve once
                .Subscribe(
                    _ =>
                    {
                        Logger.Info("[MarketAnalyzerLogic] InstrumentDbReady received - resolving NIFTY Futures contract");
                        ResolveNiftyFuturesContract();
                    },
                    ex =>
                    {
                        if (ex is TimeoutException)
                        {
                            Logger.Error("[MarketAnalyzerLogic] Timeout waiting for InstrumentDbReady (90s) - NIFTY Futures symbol will not be resolved");
                        }
                        else
                        {
                            Logger.Error($"[MarketAnalyzerLogic] Error awaiting InstrumentDbReady: {ex.Message}", ex);
                        }
                    });
        }

        /// <summary>
        /// Resolves the current month's NIFTY Futures contract using InstrumentManager.
        /// </summary>
        private void ResolveNiftyFuturesContract()
        {
            var result = InstrumentManager.Instance.LookupFuturesInSqlite("NFO-FUT", "NIFTY", DateTime.Today);

            if (result.token > 0)
            {
                NiftyFuturesToken = result.token;
                NiftyFuturesSymbol = result.symbol;
                NiftyFuturesExpiry = result.expiry;

                // Add the trading symbol to aliases for matching
                _niftyFuturesAliases.Add(NiftyFuturesSymbol);

                Logger.Info($"[MarketAnalyzerLogic] ResolveNiftyFuturesContract(): Resolved NIFTY_I -> {NiftyFuturesSymbol} (token={NiftyFuturesToken}, expiry={NiftyFuturesExpiry:yyyy-MM-dd})");
            }
            else
            {
                Logger.Warn("[MarketAnalyzerLogic] ResolveNiftyFuturesContract(): No NIFTY futures contract found");
            }
        }

        /// <summary>
        /// Checks if a symbol is a NIFTY Futures alias (starts with NIFTY and ends with FUT)
        /// </summary>
        private bool IsNiftyFuturesAlias(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            if (_niftyFuturesAliases.Contains(symbol)) return true;
            // Match pattern: starts with NIFTY, ends with FUT (e.g., NIFTY26JANFUT, NIFTY26FEBFUT)
            return symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) &&
                   symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets up System.Reactive pipeline for calculating projected opens.
        /// Uses CombineLatest to wait for all required data before calculating.
        /// </summary>
        private void SetupReactiveProjectedOpensPipeline()
        {
            // Create observables for each data point we need
            var giftChangeStream = _priceUpdateSubject
                .Where(u => u.TickerSymbol == "GIFT NIFTY" && u.NetChangePercent != 0)
                .Select(u => u.NetChangePercent)
                .DistinctUntilChanged();

            var niftyCloseStream = _priceUpdateSubject
                .Where(u => u.TickerSymbol == "NIFTY 50" && u.Close > 0)
                .Select(u => u.Close)
                .Take(1); // Only need first close value

            var sensexCloseStream = _priceUpdateSubject
                .Where(u => u.TickerSymbol == "SENSEX" && u.Close > 0)
                .Select(u => u.Close)
                .Take(1); // Only need first close value

            // Combine streams - fires when we have GIFT change% and at least one prior close
            _projectedOpenSubscription = giftChangeStream
                .CombineLatest(niftyCloseStream, sensexCloseStream, (giftChg, niftyClose, sensexClose) => new { giftChg, niftyClose, sensexClose })
                .Take(1) // Calculate projected opens ONCE
                .Subscribe(data =>
                {
                    // giftChg is in percentage form (e.g., 0.09 means 0.09%)
                    double giftChgDecimal = data.giftChg / 100.0;

                    double niftyProjOpen = data.niftyClose * (1 + giftChgDecimal);
                    double sensexProjOpen = data.sensexClose * (1 + giftChgDecimal);

                    ProjectedNiftyOpenPrice = niftyProjOpen;
                    ProjectedSensexOpenPrice = sensexProjOpen;

                    // Fire projected open update event
                    _projectedOpenSubject.OnNext(new ProjectedOpenUpdate
                    {
                        NiftyProjectedOpen = niftyProjOpen,
                        SensexProjectedOpen = sensexProjOpen,
                        GiftChangePercent = data.giftChg
                    });

                    Logger.Info($"[MarketAnalyzerLogic] Rx Pipeline: Projected Opens calculated - GIFT Chg%: {data.giftChg:+0.00;-0.00}%, NIFTY: {niftyProjOpen:F0}, SENSEX: {sensexProjOpen:F0}");
                },
                ex => Logger.Error($"[MarketAnalyzerLogic] Rx Pipeline: Error - {ex.Message}", ex));
        }

        public void NotifyHistoricalDataStatus(string symbol, string status) => HistoricalDataStatusChanged?.Invoke(symbol, status);

        public void UpdateOptionPrice(string symbol, decimal price, DateTime timestamp)
        {
            _priceCache[symbol] = (double)price;
            CheckAndFirePriceSyncReady();
        }

        public void UpdatePrice(string symbol, double price)
        {
            UpdatePrice(symbol, price, 0);
        }

        public void UpdatePrice(string symbol, double price, double priorClose)
        {
            Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): symbol='{symbol}', price={price}, priorClose={priorClose}");

            TickerData ticker = null;

            // Normalize and route to the correct static ticker
            if (GiftNiftyAliases.Contains(symbol))
            {
                GiftNiftyPrice = price;
                ticker = GiftNiftyTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0)
                {
                    GiftNiftyPriorClose = priorClose;
                    ticker.Close = priorClose;
                    Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): GiftNiftyPriorClose set to {priorClose}");
                }
            }
            else if (NiftyAliases.Contains(symbol))
            {
                NiftySpotPrice = price;
                ticker = NiftyTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0)
                {
                    ticker.Close = priorClose;
                }
            }
            else if (SensexAliases.Contains(symbol))
            {
                SensexSpotPrice = price;
                ticker = SensexTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0)
                {
                    ticker.Close = priorClose;
                }
            }
            else if (IsNiftyFuturesAlias(symbol))
            {
                NiftyFuturesPrice = price;
                ticker = NiftyFuturesTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0)
                {
                    ticker.Close = priorClose;
                }
            }
            else
            {
                // Not one of the tracked indices - just log and store in hub
                Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): Symbol '{symbol}' not a tracked index");
            }

            // Also store price in internal cache (for option chain and other lookups)
            _priceCache[symbol] = price;

            CheckAndFirePriceSyncReady();
            CheckAndCalculate();

            // Fire event with the ticker object (not just symbol string)
            if (ticker != null)
            {
                TickerUpdated?.Invoke(ticker.Symbol);

                // Emit to System.Reactive stream for event-driven updates
                _priceUpdateSubject.OnNext(new TickerPriceUpdate
                {
                    TickerSymbol = ticker.Symbol,
                    Price = ticker.CurrentPrice,
                    Close = ticker.Close,
                    NetChangePercent = ticker.NetChangePercent
                });
            }
        }

        public void UpdatePrice(string symbol, double price, DateTime timestamp)
        {
            UpdatePrice(symbol, price, 0);
        }

        /// <summary>
        /// Updates the Close (prior day close) for a symbol - does NOT affect CurrentPrice
        /// </summary>
        public void UpdateClose(string symbol, double closePrice)
        {
            Logger.Debug($"[MarketAnalyzerLogic] UpdateClose(): symbol='{symbol}', closePrice={closePrice}");

            TickerData ticker = null;

            // Normalize and route to the correct static ticker
            if (GiftNiftyAliases.Contains(symbol))
            {
                GiftNiftyPriorClose = closePrice;
                ticker = GiftNiftyTicker;
                ticker.Close = closePrice;
                Logger.Info($"[MarketAnalyzerLogic] UpdateClose(): GiftNiftyPriorClose set to {closePrice}");
                // Re-check and calculate after prior close is set
                CheckAndCalculate();
            }
            else if (NiftyAliases.Contains(symbol))
            {
                ticker = NiftyTicker;
                ticker.Close = closePrice;
            }
            else if (SensexAliases.Contains(symbol))
            {
                ticker = SensexTicker;
                ticker.Close = closePrice;
            }
            else if (IsNiftyFuturesAlias(symbol))
            {
                ticker = NiftyFuturesTicker;
                ticker.Close = closePrice;
            }
            else
            {
                Logger.Debug($"[MarketAnalyzerLogic] UpdateClose(): Symbol '{symbol}' not a tracked index");
            }

            // Emit to System.Reactive stream for event-driven updates
            if (ticker != null)
            {
                _priceUpdateSubject.OnNext(new TickerPriceUpdate
                {
                    TickerSymbol = ticker.Symbol,
                    Price = ticker.CurrentPrice,
                    Close = ticker.Close,
                    NetChangePercent = ticker.NetChangePercent
                });
            }
        }

        // Cache Facades
        public List<string> GetCachedExpiries(string underlying) => OptionGenerationService.Instance.GetCachedExpiries(underlying);
        public int GetLotSize(string underlying) => OptionGenerationService.Instance.GetLotSize(underlying);
        public int GetCachedLotSize(string underlying) => OptionGenerationService.Instance.GetLotSize(underlying);

        public async Task GenerateOptionsAsync(string underlying, double price, DateTime expiry)
        {
            var options = await OptionGenerationService.Instance.GenerateOptionsAsync(underlying, price, expiry);
            OptionsGenerated?.Invoke(options);
        }

        public void SetATMStrike(string underlying, decimal strike) => OptionGenerationService.Instance.SetATMStrike(underlying, strike);
        public decimal GetATMStrike(string underlying) => OptionGenerationService.Instance.GetATMStrike(underlying);

        public double GetPrice(string symbol)
        {
            return _priceCache.TryGetValue(symbol, out double price) ? price : 0;
        }

        /// <summary>
        /// Checks if the market is currently open for trading.
        /// Wrapper around DateTimeHelper.IsMarketOpen for convenience.
        /// </summary>
        public bool IsMarketOpen() => Helpers.DateTimeHelper.IsMarketOpen();

        /// <summary>
        /// Gets the projected open price for the specified underlying.
        /// </summary>
        public double GetProjectedOpen(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 0;
            if (underlying.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
                return ProjectedNiftyOpenPrice;
            if (underlying.Equals("SENSEX", StringComparison.OrdinalIgnoreCase))
                return ProjectedSensexOpenPrice;
            return 0;
        }

        public void Reset()
        {
            _priceCache.Clear();
            _priceSyncFired = false;
            System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);

            // Reset prices
            GiftNiftyPrice = 0;
            NiftySpotPrice = 0;
            SensexSpotPrice = 0;
            NiftyFuturesPrice = 0;
            GiftNiftyPriorClose = 0;
            ProjectedNiftyOpenPrice = 0;
            ProjectedSensexOpenPrice = 0;

            // Reset static tickers (don't remove from collection, just reset values)
            ResetTicker(GiftNiftyTicker);
            ResetTicker(NiftyTicker);
            ResetTicker(SensexTicker);
            ResetTicker(NiftyFuturesTicker);

            // Re-setup the Rx pipeline for projected opens
            _projectedOpenSubscription?.Dispose();
            SetupReactiveProjectedOpensPipeline();
        }

        private void ResetTicker(TickerData ticker)
        {
            ticker.CurrentPrice = 0;
            ticker.Open = 0;
            ticker.High = 0;
            ticker.Low = 0;
            ticker.Close = 0;
            ticker.ProjectedOpen = 0;
        }

        private void CheckAndFirePriceSyncReady()
        {
            // DEPRECATED: This method checked for NIFTY/SENSEX spot prices, but that's unreliable
            // because prices can be stored with different keys. The definitive signal is OptionsGenerated.
            // Keeping for backward compatibility but it won't fire anymore - use FirePriceSyncReadyAfterOptionsGenerated instead.
        }

        /// <summary>
        /// Fires PriceSyncReady after options are generated. This is the definitive signal that
        /// Option Chain has all the data TBS needs: underlying, expiry, DTE, ATM strikes, and prices.
        /// </summary>
        private void FirePriceSyncReadyAfterOptionsGenerated(string underlying, DateTime expiry, int dte, double atmStrike)
        {
            lock (_syncLock)
            {
                if (_priceSyncFired) return;
                _priceSyncFired = true;

                Logger.Info($"[MarketAnalyzerLogic] PriceSyncReady fired after OptionsGenerated: Underlying={underlying}, Expiry={expiry:dd-MMM-yyyy}, DTE={dte}, ATM={atmStrike}");

                // Publish to reactive hub (primary - uses ReplaySubject for late subscribers)
                MarketDataReactiveHub.Instance.PublishPriceSyncReady();

                // Fire legacy event for backward compatibility
                PriceSyncReady?.Invoke();

                // Send Telegram alert for Option Chain generated
                TelegramAlertService.Instance.SendOptionChainAlert(underlying, dte, atmStrike);
            }
        }

        /// <summary>
        /// Checks if we have enough price data to calculate projected opens and trigger option generation.
        /// Called after each price update.
        /// </summary>
        private void CheckAndCalculate()
        {
            double giftPrice = GiftNiftyPrice;
            double niftyPrice = NiftySpotPrice;
            double sensexPrice = SensexSpotPrice;

            Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): GIFT={giftPrice}, NIFTY={niftyPrice}, SENSEX={sensexPrice}, PriorClose={GiftNiftyPriorClose}");

            // We need at least GIFT NIFTY and one Spot to proceed
            if (giftPrice <= 0)
            {
                Logger.Debug("[MarketAnalyzerLogic] CheckAndCalculate(): GIFT NIFTY price not available yet, waiting...");
                return;
            }

            // Calculate change percent from GIFT NIFTY
            double changePercent = 0.0;
            if (GiftNiftyPriorClose > 0)
            {
                changePercent = (giftPrice - GiftNiftyPriorClose) / GiftNiftyPriorClose;
                Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): Change% = ({giftPrice} - {GiftNiftyPriorClose}) / {GiftNiftyPriorClose} = {changePercent:P4}");
            }
            else
            {
                Logger.Warn("[MarketAnalyzerLogic] CheckAndCalculate(): GIFT NIFTY PriorClose not available, using 0% change");
                StatusUpdated?.Invoke($"Waiting for GIFT NIFTY Prior Close... (Current: {giftPrice})");
            }

            // Calculate Projected Opens
            double niftyProjected = niftyPrice > 0 ? niftyPrice * (1 + changePercent) : 0;
            double sensexProjected = sensexPrice > 0 ? sensexPrice * (1 + changePercent) : 0;

            // Store projected values
            ProjectedNiftyOpenPrice = niftyProjected;
            ProjectedSensexOpenPrice = sensexProjected;

            // Update ticker projected values if they exist
            var niftyTicker = NiftyTicker;
            var sensexTicker = SensexTicker;
            if (niftyTicker != null) niftyTicker.ProjectedOpen = niftyProjected;
            if (sensexTicker != null) sensexTicker.ProjectedOpen = sensexProjected;

            Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): Projected Opens - NIFTY={niftyProjected:F2}, SENSEX={sensexProjected:F2}");
            // NOTE: Removed StatusUpdated call here - it was causing flashing text in the status bar every tick

            // Trigger option generation if we have projected prices and haven't generated yet
            // Use Interlocked.CompareExchange to atomically check-and-set to prevent race condition
            // where multiple threads see _optionsAlreadyGenerated=0 and all call GenerateOptions()
            if ((niftyProjected > 0 || sensexProjected > 0) &&
                System.Threading.Interlocked.CompareExchange(ref _optionsAlreadyGenerated, 1, 0) == 0)
            {
                Logger.Info("[MarketAnalyzerLogic] CheckAndCalculate(): Triggering option generation...");
                GenerateOptions(niftyProjected, sensexProjected);
            }
            else if (_optionsAlreadyGenerated == 1)
            {
                Logger.Debug("[MarketAnalyzerLogic] CheckAndCalculate(): Options already generated, skipping");
            }
        }

        /// <summary>
        /// Generates option symbols based on DTE priority:
        /// 1. NIFTY 0DTE, 2. SENSEX 0DTE, 3. NIFTY 1DTE, 4. SENSEX 1DTE, 5. Default NIFTY
        /// Awaits InstrumentDbReadyStream before performing database lookups.
        /// </summary>
        private async void GenerateOptions(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Starting with niftyProjected={niftyProjected:F2}, sensexProjected={sensexProjected:F2}");

            try
            {
                // Flag already set by Interlocked.CompareExchange in CheckAndCalculate()
                // No need to set again here

                // Await instrument database ready signal before performing lookups
                // This ensures the SQLite database is loaded before we query expiries and options
                try
                {
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Awaiting InstrumentDbReady signal...");
                    var dbReady = await MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                        .Timeout(TimeSpan.FromSeconds(60))
                        .FirstAsync()
                        .ToTask();

                    if (!dbReady)
                    {
                        Logger.Error("[MarketAnalyzerLogic] GenerateOptions(): Instrument DB not ready - cannot generate options");
                        System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                        StatusUpdated?.Invoke("Error: Instrument database not ready");
                        return;
                    }
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): InstrumentDbReady signal received, proceeding with option generation");
                }
                catch (TimeoutException)
                {
                    Logger.Error("[MarketAnalyzerLogic] GenerateOptions(): Timeout waiting for InstrumentDbReady (60s)");
                    System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                    StatusUpdated?.Invoke("Error: Timeout waiting for instrument database");
                    return;
                }

                // Fetch expiries from database
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Fetching NIFTY expiries from database...");
                var niftyExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("NIFTY");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Found {niftyExpiries.Count} NIFTY expiries");

                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Fetching SENSEX expiries from database...");
                var sensexExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("SENSEX");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Found {sensexExpiries.Count} SENSEX expiries");

                // Cache expiries
                lock (_expiryLock)
                {
                    _cachedExpiries["NIFTY"] = new List<DateTime>(niftyExpiries);
                    _cachedExpiries["SENSEX"] = new List<DateTime>(sensexExpiries);
                }

                // Fetch and cache lot sizes
                var niftyLotSize = InstrumentManager.Instance.GetLotSizeForUnderlying("NIFTY");
                var sensexLotSize = InstrumentManager.Instance.GetLotSizeForUnderlying("SENSEX");
                lock (_lotSizeLock)
                {
                    if (niftyLotSize > 0) _cachedLotSizes["NIFTY"] = niftyLotSize;
                    if (sensexLotSize > 0) _cachedLotSizes["SENSEX"] = sensexLotSize;
                }
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Lot sizes - NIFTY={niftyLotSize}, SENSEX={sensexLotSize}");

                // Get nearest expiries
                var niftyNear = GetNearestExpiry(niftyExpiries);
                var sensexNear = GetNearestExpiry(sensexExpiries);

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Nearest expiries - NIFTY={niftyNear:yyyy-MM-dd}, SENSEX={sensexNear:yyyy-MM-dd}");

                // Calculate DTE
                double niftyDTE = (niftyNear.Date - DateTime.Today).TotalDays;
                double sensexDTE = (sensexNear.Date - DateTime.Today).TotalDays;
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): DTE - NIFTY={niftyDTE}, SENSEX={sensexDTE}");

                // DTE-based priority selection
                string selectedUnderlying;
                DateTime selectedExpiry;
                int stepSize;
                double projectedPrice;

                if (niftyDTE == 0)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected NIFTY 0DTE (Priority 1)");
                }
                else if (sensexDTE == 0)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    projectedPrice = sensexProjected;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected SENSEX 0DTE (Priority 2)");
                }
                else if (niftyDTE == 1)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected NIFTY 1DTE (Priority 3)");
                }
                else if (sensexDTE == 1)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    projectedPrice = sensexProjected;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected SENSEX 1DTE (Priority 4)");
                }
                else
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Default to NIFTY (DTE={niftyDTE})");
                }

                // Wait for projected price if not available
                double minValidPrice = selectedUnderlying == "NIFTY" ? 1000 : 10000;
                if (projectedPrice < minValidPrice)
                {
                    Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Waiting for {selectedUnderlying} projected price (current={projectedPrice:F0}, min={minValidPrice})...");
                    StatusUpdated?.Invoke($"Waiting for {selectedUnderlying} price data...");

                    int waitedMs = 0;
                    const int maxWaitMs = 30000;
                    const int pollIntervalMs = 500;

                    while (waitedMs < maxWaitMs)
                    {
                        await Task.Delay(pollIntervalMs);
                        waitedMs += pollIntervalMs;

                        double spotPrice = selectedUnderlying == "NIFTY" ? NiftySpotPrice : SensexSpotPrice;
                        if (spotPrice > 0 && GiftNiftyPriorClose > 0 && GiftNiftyPrice > 0)
                        {
                            double changePercent = (GiftNiftyPrice - GiftNiftyPriorClose) / GiftNiftyPriorClose;
                            projectedPrice = spotPrice * (1 + changePercent);
                        }

                        if (projectedPrice >= minValidPrice)
                        {
                            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Got {selectedUnderlying} price={projectedPrice:F0} after {waitedMs}ms");
                            break;
                        }
                    }

                    if (projectedPrice < minValidPrice)
                    {
                        Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Timeout waiting for {selectedUnderlying} price - aborting");
                        System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                        StatusUpdated?.Invoke($"Timeout: No {selectedUnderlying} price received");
                        return;
                    }
                }

                // Round projected price to step size for ATM strike
                double atmStrike = Math.Round(projectedPrice / stepSize) * stepSize;

                // Determine if monthly expiry
                var allExpiries = selectedUnderlying == "NIFTY" ? niftyExpiries : sensexExpiries;
                bool isMonthlyExpiry = IsMonthlyExpiry(selectedExpiry, allExpiries);

                // Store selection
                SelectedUnderlying = selectedUnderlying;
                SelectedExpiry = selectedExpiry.ToString("dd-MMM-yyyy");
                SelectedIsMonthlyExpiry = isMonthlyExpiry;

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Selected {selectedUnderlying} {selectedExpiry:yyyy-MM-dd} ATM={atmStrike} (Monthly={isMonthlyExpiry})");
                StatusUpdated?.Invoke($"Selected: {selectedUnderlying} {selectedExpiry:ddMMM} at Strike {atmStrike}");

                // Generate options (ATM +/- 30 = 61 strikes x 2 types = 122 options)
                var generated = new List<MappedInstrument>();
                string segment = selectedUnderlying == "SENSEX" ? "BFO-OPT" : "NFO-OPT";
                int lotSize = _cachedLotSizes.TryGetValue(selectedUnderlying, out var ls) ? ls : (selectedUnderlying == "NIFTY" ? 50 : 10);

                for (int i = -30; i <= 30; i++)
                {
                    double strike = atmStrike + (i * stepSize);

                    // Lookup CE
                    var (ceToken, ceSymbol) = InstrumentManager.Instance.LookupOptionDetails(segment, selectedUnderlying, selectedExpiry.ToString("yyyy-MM-dd"), strike, "CE");
                    if (ceToken > 0)
                    {
                        generated.Add(CreateOption(selectedUnderlying, selectedExpiry, strike, "CE", ceToken, ceSymbol, segment, lotSize, isMonthlyExpiry));
                    }

                    // Lookup PE
                    var (peToken, peSymbol) = InstrumentManager.Instance.LookupOptionDetails(segment, selectedUnderlying, selectedExpiry.ToString("yyyy-MM-dd"), strike, "PE");
                    if (peToken > 0)
                    {
                        generated.Add(CreateOption(selectedUnderlying, selectedExpiry, strike, "PE", peToken, peSymbol, segment, lotSize, isMonthlyExpiry));
                    }
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Generated {generated.Count} option symbols");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Strike range = {atmStrike - (30 * stepSize)} to {atmStrike + (30 * stepSize)}");

                // Publish to reactive hub (primary - OptionChainWindow uses this)
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Publishing to MarketDataReactiveHub...");
                int dte = selectedUnderlying == "NIFTY" ? (int)niftyDTE : (int)sensexDTE;
                MarketDataReactiveHub.Instance.PublishOptionsGenerated(
                    generated,
                    selectedUnderlying,
                    selectedExpiry,
                    dte,
                    atmStrike,
                    projectedPrice);

                // Fire legacy event for backward compatibility
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Invoking OptionsGenerated event...");
                OptionsGenerated?.Invoke(generated);

                // Fire PriceSyncReady AFTER options are generated - this is the definitive signal
                // that Option Chain has underlying, expiry, DTE, ATM strikes, and is ready for TBS
                FirePriceSyncReadyAfterOptionsGenerated(selectedUnderlying, selectedExpiry, dte, atmStrike);

                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Exception - {ex.Message}", ex);
                StatusUpdated?.Invoke("Error generating options: " + ex.Message);
                System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
            }
        }

        private MappedInstrument CreateOption(string underlying, DateTime expiry, double strike, string type, long token, string symbol, string segment, int lotSize, bool isMonthlyExpiry)
        {
            return new MappedInstrument
            {
                symbol = symbol,
                zerodhaSymbol = symbol,
                underlying = underlying,
                expiry = expiry,
                strike = strike,
                option_type = type,
                segment = segment,
                tick_size = 0.05,
                lot_size = lotSize,
                instrument_token = token
            };
        }

        private DateTime GetNearestExpiry(List<DateTime> expiries)
        {
            var nearest = expiries.Where(e => e.Date >= DateTime.Today).OrderBy(e => e).FirstOrDefault();
            Logger.Debug($"[MarketAnalyzerLogic] GetNearestExpiry(): Found {nearest:yyyy-MM-dd} from {expiries.Count} expiries");
            return nearest;
        }

        private bool IsMonthlyExpiry(DateTime expiry, List<DateTime> allExpiries)
        {
            var sameMonthExpiries = allExpiries
                .Where(e => e.Year == expiry.Year && e.Month == expiry.Month)
                .OrderBy(e => e)
                .ToList();

            if (sameMonthExpiries.Count == 0) return true;

            DateTime lastExpiry = sameMonthExpiries.Last();
            return expiry.Date == lastExpiry.Date;
        }

        /// <summary>
        /// Resets the option generation flag to allow re-generation
        /// </summary>
        public void ResetOptionsGeneration()
        {
            System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
            Logger.Info("[MarketAnalyzerLogic] ResetOptionsGeneration(): Options can now be regenerated");
        }

        /// <summary>
        /// Triggers option generation from external source (e.g., MarketDataReactiveHub).
        /// Called when projected opens are ready via reactive stream.
        /// </summary>
        public void TriggerOptionsGeneration(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] TriggerOptionsGeneration(): Called with NIFTY={niftyProjected:F0}, SENSEX={sensexProjected:F0}");

            // Store projected values
            ProjectedNiftyOpenPrice = niftyProjected;
            ProjectedSensexOpenPrice = sensexProjected;

            // Update ticker projected values
            if (NiftyTicker != null) NiftyTicker.ProjectedOpen = niftyProjected;
            if (SensexTicker != null) SensexTicker.ProjectedOpen = sensexProjected;

            // Use Interlocked to prevent duplicate generation
            if (System.Threading.Interlocked.CompareExchange(ref _optionsAlreadyGenerated, 1, 0) == 0)
            {
                Logger.Info("[MarketAnalyzerLogic] TriggerOptionsGeneration(): Starting option generation...");
                GenerateOptionsAndPublishToHub(niftyProjected, sensexProjected);
            }
            else
            {
                Logger.Debug("[MarketAnalyzerLogic] TriggerOptionsGeneration(): Options already generated, skipping");
            }
        }

        /// <summary>
        /// Generates options and publishes to MarketDataReactiveHub.
        /// Awaits InstrumentDbReadyStream before performing database lookups.
        /// </summary>
        private async void GenerateOptionsAndPublishToHub(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Starting with niftyProjected={niftyProjected:F2}, sensexProjected={sensexProjected:F2}");

            try
            {
                // Await instrument database ready signal before performing lookups
                try
                {
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Awaiting InstrumentDbReady signal...");
                    var dbReady = await MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                        .Timeout(TimeSpan.FromSeconds(60))
                        .FirstAsync()
                        .ToTask();

                    if (!dbReady)
                    {
                        Logger.Error("[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Instrument DB not ready - cannot generate options");
                        System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                        return;
                    }
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): InstrumentDbReady signal received, proceeding");
                }
                catch (TimeoutException)
                {
                    Logger.Error("[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Timeout waiting for InstrumentDbReady (60s)");
                    System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                    return;
                }

                // Fetch expiries from database
                var niftyExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("NIFTY");
                var sensexExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("SENSEX");

                // Cache expiries
                lock (_expiryLock)
                {
                    _cachedExpiries["NIFTY"] = new List<DateTime>(niftyExpiries);
                    _cachedExpiries["SENSEX"] = new List<DateTime>(sensexExpiries);
                }

                // Fetch and cache lot sizes
                var niftyLotSize = InstrumentManager.Instance.GetLotSizeForUnderlying("NIFTY");
                var sensexLotSize = InstrumentManager.Instance.GetLotSizeForUnderlying("SENSEX");
                lock (_lotSizeLock)
                {
                    if (niftyLotSize > 0) _cachedLotSizes["NIFTY"] = niftyLotSize;
                    if (sensexLotSize > 0) _cachedLotSizes["SENSEX"] = sensexLotSize;
                }

                // Get nearest expiries
                var niftyNear = GetNearestExpiry(niftyExpiries);
                var sensexNear = GetNearestExpiry(sensexExpiries);

                // Calculate DTE
                double niftyDTE = (niftyNear.Date - DateTime.Today).TotalDays;
                double sensexDTE = (sensexNear.Date - DateTime.Today).TotalDays;

                // DTE-based priority selection
                string selectedUnderlying;
                DateTime selectedExpiry;
                int stepSize;
                double projectedPrice;
                int dte;

                if (niftyDTE == 0)
                {
                    selectedUnderlying = "NIFTY"; selectedExpiry = niftyNear; stepSize = 50; projectedPrice = niftyProjected; dte = 0;
                }
                else if (sensexDTE == 0)
                {
                    selectedUnderlying = "SENSEX"; selectedExpiry = sensexNear; stepSize = 100; projectedPrice = sensexProjected; dte = 0;
                }
                else if (niftyDTE == 1)
                {
                    selectedUnderlying = "NIFTY"; selectedExpiry = niftyNear; stepSize = 50; projectedPrice = niftyProjected; dte = 1;
                }
                else if (sensexDTE == 1)
                {
                    selectedUnderlying = "SENSEX"; selectedExpiry = sensexNear; stepSize = 100; projectedPrice = sensexProjected; dte = 1;
                }
                else
                {
                    selectedUnderlying = "NIFTY"; selectedExpiry = niftyNear; stepSize = 50; projectedPrice = niftyProjected; dte = (int)niftyDTE;
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Selected {selectedUnderlying} DTE={dte}");

                // Round projected price to step size for ATM strike
                double atmStrike = Math.Round(projectedPrice / stepSize) * stepSize;

                // Determine if monthly expiry
                var allExpiries = selectedUnderlying == "NIFTY" ? niftyExpiries : sensexExpiries;
                bool isMonthlyExpiry = IsMonthlyExpiry(selectedExpiry, allExpiries);

                // Store selection
                SelectedUnderlying = selectedUnderlying;
                SelectedExpiry = selectedExpiry.ToString("dd-MMM-yyyy");
                SelectedIsMonthlyExpiry = isMonthlyExpiry;

                // Generate options (ATM +/- 30 = 61 strikes x 2 types = 122 options)
                var generated = new List<MappedInstrument>();
                string segment = selectedUnderlying == "SENSEX" ? "BFO-OPT" : "NFO-OPT";
                int lotSize = _cachedLotSizes.TryGetValue(selectedUnderlying, out var ls) ? ls : (selectedUnderlying == "NIFTY" ? 50 : 10);

                for (int i = -30; i <= 30; i++)
                {
                    double strike = atmStrike + (i * stepSize);

                    var (ceToken, ceSymbol) = InstrumentManager.Instance.LookupOptionDetails(segment, selectedUnderlying, selectedExpiry.ToString("yyyy-MM-dd"), strike, "CE");
                    if (ceToken > 0)
                        generated.Add(CreateOption(selectedUnderlying, selectedExpiry, strike, "CE", ceToken, ceSymbol, segment, lotSize, isMonthlyExpiry));

                    var (peToken, peSymbol) = InstrumentManager.Instance.LookupOptionDetails(segment, selectedUnderlying, selectedExpiry.ToString("yyyy-MM-dd"), strike, "PE");
                    if (peToken > 0)
                        generated.Add(CreateOption(selectedUnderlying, selectedExpiry, strike, "PE", peToken, peSymbol, segment, lotSize, isMonthlyExpiry));
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Generated {generated.Count} options, ATM={atmStrike}");

                // Publish to reactive hub
                MarketDataReactiveHub.Instance.PublishOptionsGenerated(
                    generated,
                    selectedUnderlying,
                    selectedExpiry,
                    dte,
                    atmStrike,
                    projectedPrice);

                // Also fire legacy event for backward compatibility
                OptionsGenerated?.Invoke(generated);

                // Initialize hedge subscriptions (liquid strikes for hedge selection)
                // NIFTY: multiples of 100, SENSEX: multiples of 500
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HedgeSubscriptionService.Instance.InitializeAsync(
                            selectedUnderlying,
                            selectedExpiry,
                            (decimal)atmStrike,
                            isMonthlyExpiry);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MarketAnalyzerLogic] HedgeSubscriptionService initialization failed: {ex.Message}");
                    }
                });

                // Fire PriceSyncReady AFTER options are generated - this is the definitive signal
                // that Option Chain has underlying, expiry, DTE, ATM strikes, and is ready for TBS
                FirePriceSyncReadyAfterOptionsGenerated(selectedUnderlying, selectedExpiry, dte, atmStrike);

                Logger.Info("[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Exception - {ex.Message}", ex);
                System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
            }
        }
    }

    public class TickerData : INotifyPropertyChanged
    {
        private string _symbol;
        private double _currentPrice;
        private double _open;
        private double _high;
        private double _low;
        private double _close;
        private double _netChange;
        private double _netChangePercent;
        private double _projectedOpen;
        private DateTime _lastUpdate;

        public string Symbol { get => _symbol; set { _symbol = value; OnPropertyChanged(); } }
        public double CurrentPrice
        {
            get => _currentPrice;
            set
            {
                _currentPrice = value;
                _lastUpdate = DateTime.Now;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastPriceDisplay));
                OnPropertyChanged(nameof(LastUpdateTimeDisplay));
                OnPropertyChanged(nameof(IsPositive));
                // Recompute change if Close is available
                if (_close > 0)
                {
                    NetChange = _currentPrice - _close;
                    NetChangePercent = (NetChange / _close) * 100;
                }
            }
        }

        public double Open { get => _open; set { _open = value; OnPropertyChanged(); } }
        public double High { get => _high; set { _high = value; OnPropertyChanged(); } }
        public double Low { get => _low; set { _low = value; OnPropertyChanged(); } }
        public double Close
        {
            get => _close;
            set
            {
                _close = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PriorCloseDisplay));
                // Recompute change when Close is set
                if (_currentPrice > 0 && _close > 0)
                {
                    NetChange = _currentPrice - _close;
                    NetChangePercent = (NetChange / _close) * 100;
                }
            }
        }

        // NetChange and NetChangePercent - can be set directly from API or computed
        public double NetChange
        {
            get => _netChange;
            set { _netChange = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangeDisplay)); OnPropertyChanged(nameof(IsPositive)); }
        }

        public double NetChangePercent
        {
            get => _netChangePercent;
            set { _netChangePercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangePercentDisplay)); }
        }

        public double ProjectedOpen
        {
            get => _projectedOpen;
            set
            {
                _projectedOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProjectedOpenDisplay));
            }
        }

        // Alias properties for compatibility
        public double LastPrice { get => CurrentPrice; set => CurrentPrice = value; }
        public DateTime LastUpdateTime { get => _lastUpdate; set { _lastUpdate = value; OnPropertyChanged(); } }

        // Display properties
        public string LastPriceDisplay => CurrentPrice.ToString("F2");
        public string PriorCloseDisplay => Close > 0 ? Close.ToString("F2") : "-";
        public string NetChangeDisplay => NetChange != 0 ? $"{NetChange:+0.00;-0.00;0.00}" : "---";
        public string NetChangePercentDisplay => NetChangePercent != 0 ? $"{NetChangePercent:+0.00;-0.00;0.00}%" : "---";
        public string LastUpdateTimeDisplay => _lastUpdate.ToString("HH:mm:ss");
        public bool IsPositive => NetChange >= 0;
        public string ProjectedOpenDisplay => ProjectedOpen > 0 ? ProjectedOpen.ToString("F2") : "-";

        public void UpdatePrice(double price)
        {
            if (Open == 0) Open = price;
            if (price > High) High = price;
            if (Low == 0 || price < Low) Low = price;
            CurrentPrice = price;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Data class for System.Reactive price update events
    /// </summary>
    public class TickerPriceUpdate
    {
        public string TickerSymbol { get; set; }
        public double Price { get; set; }
        public double Close { get; set; }
        public double NetChangePercent { get; set; }
    }

    /// <summary>
    /// Data class for System.Reactive projected open calculation events
    /// </summary>
    public class ProjectedOpenUpdate
    {
        public double NiftyProjectedOpen { get; set; }
        public double SensexProjectedOpen { get; set; }
        public double GiftChangePercent { get; set; }
    }
}
