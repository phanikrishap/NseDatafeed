using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.MarketData;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Orchestrates the Market Analyzer workflow: subscribes to indicator symbols,
    /// calculates projected opens, and triggers option symbol generation/subscription.
    /// </summary>
    public class MarketAnalyzerService
    {
        private static MarketAnalyzerService _instance;
        public static MarketAnalyzerService Instance => _instance ?? (_instance = new MarketAnalyzerService());

        private readonly MarketAnalyzerLogic _logic;
        private readonly SubscriptionManager _subscriptionManager;
        private readonly MarketDataReactiveHub _hub;
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();
        private readonly ConcurrentDictionary<string, byte> _persistenceSubscribedSymbols = new ConcurrentDictionary<string, byte>();
        private bool _isRunning = false;

        private MarketAnalyzerService()
        {
            _logic = MarketAnalyzerLogic.Instance;
            _subscriptionManager = SubscriptionManager.Instance;
            _hub = MarketDataReactiveHub.Instance;

            Logger.Info("[MarketAnalyzerService] Constructor: Initializing singleton instance");

            // Wire up reactive streams
            SetupReactiveSubscriptions();

            Logger.Info("[MarketAnalyzerService] Constructor: Reactive subscriptions wired up successfully");
        }

        /// <summary>
        /// Sets up reactive subscriptions to hub streams.
        /// </summary>
        private void SetupReactiveSubscriptions()
        {
            // Subscribe to OptionsGenerated stream from hub
            _subscriptions.Add(
                _hub.OptionsGeneratedStream
                    .Subscribe(
                        evt => OnOptionsGenerated(evt),
                        ex => Logger.Error($"[MarketAnalyzerService] OptionsGeneratedStream error: {ex.Message}", ex)));

            // Subscribe to ProjectedOpen stream to trigger option generation
            _subscriptions.Add(
                _hub.ProjectedOpenStream
                    .Where(state => state.IsComplete)
                    .Take(1)
                    .Subscribe(
                        state => TriggerOptionGeneration(state),
                        ex => Logger.Error($"[MarketAnalyzerService] ProjectedOpenStream error: {ex.Message}", ex)));

            Logger.Info("[MarketAnalyzerService] Reactive subscriptions configured");
        }

        /// <summary>
        /// Triggers option generation when projected opens are ready.
        /// </summary>
        private void TriggerOptionGeneration(ProjectedOpenState state)
        {
            Logger.Info($"[MarketAnalyzerService] TriggerOptionGeneration: Projected opens ready - NIFTY={state.NiftyProjectedOpen:F0}, SENSEX={state.SensexProjectedOpen:F0}");

            // Delegate to logic for actual generation
            Task.Run(() =>
            {
                try
                {
                    _logic.TriggerOptionsGeneration(state.NiftyProjectedOpen, state.SensexProjectedOpen);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] TriggerOptionGeneration error: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Starts the Market Analyzer service - subscribes to GIFT NIFTY, NIFTY 50, and SENSEX
        /// </summary>
        public void Start()
        {
            Logger.Info("[MarketAnalyzerService] Start() called");

            if (_isRunning)
            {
                Logger.Warn("[MarketAnalyzerService] Start(): Already running, skipping");
                return;
            }

            _isRunning = true;
            Logger.Info("[MarketAnalyzerService] Start(): Service started, subscribing to indicator symbols...");

            // Subscribe to GIFT_NIFTY via direct WebSocket (bypasses NT instrument requirement)
            // GIFT_NIFTY is from NSE International Exchange, not a native NT instrument type
            SubscribeToIndexViaWebSocket("GIFT_NIFTY");

            // Subscribe to NIFTY and SENSEX via standard NT instrument approach (they exist as NT instruments)
            SubscribeToIndicator("NIFTY");
            SubscribeToIndicator("SENSEX");

            // Subscribe to NIFTY Futures (NIFTY_I) - uses dynamically resolved symbol (e.g., NIFTY26JANFUT)
            // Use Rx to await InstrumentDbReady before subscribing - this ensures the symbol is resolved
            SubscribeToNiftyFuturesWhenReady();

            Logger.Info("[MarketAnalyzerService] Start(): Subscription requests queued for all indicators");
        }

        /// <summary>
        /// Subscribes to a single indicator symbol for real-time price updates.
        /// Will wait for adapter connection and create instrument if needed.
        /// </summary>
        private void SubscribeToIndicator(string symbol)
        {
            Logger.Info($"[MarketAnalyzerService] SubscribeToIndicator(): Attempting to subscribe to '{symbol}'");

            Task.Run(async () => {
                try
                {
                    // Wait for adapter to be available (max 30 seconds)
                    Logger.Debug($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Waiting for adapter connection...");
                    int maxWaitMs = 30000;
                    int waitedMs = 0;
                    while (waitedMs < maxWaitMs)
                    {
                        var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
                        if (adapter != null)
                        {
                            Logger.Info($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Adapter connected after {waitedMs}ms");
                            break;
                        }
                        await Task.Delay(500);
                        waitedMs += 500;
                    }

                    if (waitedMs >= maxWaitMs)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Timeout waiting for adapter connection");
                        return;
                    }

                    // Give instruments time to be created by the adapter
                    await Task.Delay(2000);

                    Logger.Debug($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Getting NT instrument on UI thread");

                    Instrument ntInstrument = null;

                    // Try to get or create instrument on UI thread
                    await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() => {
                        Logger.Debug($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Inside dispatcher, calling Instrument.GetInstrument()");
                        ntInstrument = Instrument.GetInstrument(symbol);

                        // If instrument doesn't exist, try to create it from mapped_instruments.json
                        if (ntInstrument == null)
                        {
                            Logger.Info($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Instrument not found, attempting to create from mapping...");
                            ntInstrument = CreateInstrumentFromMapping(symbol);
                        }
                    });

                    if (ntInstrument != null)
                    {
                        string instrumentName = ntInstrument.MasterInstrument.Name;
                        Logger.Info($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Instrument ready - MasterInstrument.Name='{instrumentName}'");

                        SubscribeToIndicatorWithClosure(ntInstrument, instrumentName);
                        Logger.Info($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Successfully subscribed to market data");
                    }
                    else
                    {
                        Logger.Warn($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Failed to get or create instrument - symbol not found");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] SubscribeToIndicator({symbol}): Exception occurred - {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Subscribes to an index symbol (like GIFT_NIFTY) via direct WebSocket.
        /// This bypasses the NT Instrument requirement since index symbols like GIFT_NIFTY
        /// are not tradeable instruments in NinjaTrader.
        /// The token is resolved from index_mappings.json via SymbolMappingService.
        /// </summary>
        private void SubscribeToIndexViaWebSocket(string symbol)
        {
            Logger.Info($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket(): Subscribing to '{symbol}' via direct WebSocket");

            Task.Run(async () =>
            {
                try
                {
                    // Wait for adapter to be available
                    Logger.Debug($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Waiting for adapter connection...");
                    int maxWaitMs = 30000;
                    int waitedMs = 0;
                    while (waitedMs < maxWaitMs)
                    {
                        var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
                        if (adapter != null)
                        {
                            Logger.Info($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Adapter connected after {waitedMs}ms");
                            break;
                        }
                        await Task.Delay(500);
                        waitedMs += 500;
                    }

                    if (waitedMs >= maxWaitMs)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Timeout waiting for adapter connection");
                        return;
                    }

                    // Get the token from InstrumentManager (loaded from index_mappings.json)
                    long token = InstrumentManager.Instance.GetInstrumentToken(symbol);
                    if (token == 0)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Token not found in index_mappings.json!");
                        return;
                    }

                    Logger.Info($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Token={token}, subscribing via SharedWebSocket");

                    // Subscribe via MarketDataService (uses SharedWebSocketService)
                    var marketDataService = MarketDataService.Instance;

                    // Track prior close for change calculation
                    double currentClose = 0;

                    bool subscribed = await marketDataService.SubscribeToSymbolDirectAsync(
                        symbol,
                        (int)token,
                        isIndex: true,  // GIFT_NIFTY is an index
                        onTick: (price, volume, timestamp) =>
                        {
                            Logger.Debug($"[MarketAnalyzerService] {symbol} Tick: price={price}");

                            // Publish to reactive hub (primary)
                            _hub.PublishIndexPrice(symbol, price, currentClose);

                            // Also update legacy logic for backward compatibility
                            _logic.UpdatePrice(symbol, price);
                        });

                    if (subscribed)
                    {
                        Logger.Info($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Successfully subscribed to WebSocket");

                        // Subscribe for NinjaTrader database persistence
                        // This registers a NT callback so ticks are saved to the database
                        SubscribeForPersistence(symbol);

                        // Request historical data to get prior close
                        await RequestIndexHistoricalData(symbol);
                    }
                    else
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): WebSocket subscription failed");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] SubscribeToIndexViaWebSocket({symbol}): Exception - {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Requests historical data for an index symbol (via direct API, no NT Instrument needed)
        /// </summary>
        private async Task RequestIndexHistoricalData(string symbol)
        {
            try
            {
                Logger.Info($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): Requesting 1min data for 5 days");
                _logic.NotifyHistoricalDataStatus(symbol, "Requesting...");

                // Calculate date range: 5 days back from now
                DateTime toDate = DateTime.Now;
                DateTime fromDate = toDate.AddDays(-5);

                // Use HistoricalDataService to fetch data from Zerodha API
                var historicalDataService = HistoricalDataService.Instance;
                var records = await historicalDataService.GetHistoricalTrades(
                    BarsPeriodType.Minute,
                    symbol,
                    fromDate,
                    toDate,
                    MarketType.Spot,  // Index is treated as spot
                    null);

                if (records != null && records.Count > 0)
                {
                    Logger.Info($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): Success - received {records.Count} bars");
                    _logic.NotifyHistoricalDataStatus(symbol, $"Done ({records.Count})");

                    // Extract last price and prior close
                    var sortedRecords = records.OrderBy(r => r.TimeStamp).ToList();
                    var lastRecord = sortedRecords.Last();
                    double lastPrice = lastRecord.Close;

                    // Find prior close - the close of the previous trading day
                    double priorClose = 0;
                    DateTime lastTradingDay = lastRecord.TimeStamp.Date;

                    var priorDayRecord = sortedRecords
                        .Where(r => r.TimeStamp.Date < lastTradingDay)
                        .OrderByDescending(r => r.TimeStamp)
                        .FirstOrDefault();

                    if (priorDayRecord != null)
                    {
                        priorClose = priorDayRecord.Close;
                        Logger.Info($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): PriorClose={priorClose} from {priorDayRecord.TimeStamp:yyyy-MM-dd HH:mm}");
                    }
                    else
                    {
                        Logger.Warn($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): Could not find prior day close");
                    }

                    Logger.Info($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): LastPrice={lastPrice}, PriorClose={priorClose}");

                    // Update both hub and legacy logic
                    if (priorClose > 0)
                    {
                        _hub.PublishIndexClose(symbol, priorClose);
                        _logic.UpdateClose(symbol, priorClose);
                    }

                    _hub.PublishIndexPrice(symbol, lastPrice, priorClose);
                    _logic.UpdatePrice(symbol, lastPrice);
                }
                else
                {
                    Logger.Warn($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): No data received");
                    _logic.NotifyHistoricalDataStatus(symbol, "No Data");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerService] RequestIndexHistoricalData({symbol}): Exception - {ex.Message}", ex);
                _logic.NotifyHistoricalDataStatus(symbol, "Error");
            }
        }

        /// <summary>
        /// Creates an NT instrument from the mapped_instruments.json data
        /// </summary>
        private Instrument CreateInstrumentFromMapping(string symbol)
        {
            try
            {
                // Get the mapping from InstrumentManager
                var mapping = InstrumentManager.Instance.GetMappingByNtSymbol(symbol);
                if (mapping == null)
                {
                    Logger.Warn($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): No mapping found in mapped_instruments.json");
                    return null;
                }

                Logger.Info($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Found mapping - token={mapping.instrument_token}, zerodha={mapping.zerodhaSymbol}");

                // Create an InstrumentDefinition for InstrumentManager
                var instrumentDef = new InstrumentDefinition
                {
                    Symbol = symbol,
                    Segment = "NSE",
                    TickSize = mapping.tick_size > 0 ? mapping.tick_size : 0.05
                };

                string ntName;
                bool created = InstrumentManager.Instance.CreateInstrument(instrumentDef, out ntName);

                if (created)
                {
                    Logger.Info($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Successfully created NT instrument '{ntName}'");

                    // NinjaTrader needs time to register the instrument after creation
                    // Retry with delay to wait for registration to complete
                    Instrument result = null;
                    int maxRetries = 10;
                    int retryDelayMs = 100;

                    for (int i = 0; i < maxRetries; i++)
                    {
                        result = Instrument.GetInstrument(ntName);
                        if (result != null)
                        {
                            Logger.Info($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Instrument available after {i * retryDelayMs}ms");
                            return result;
                        }

                        if (i < maxRetries - 1)
                        {
                            Logger.Debug($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Instrument not yet available, retry {i + 1}/{maxRetries}...");
                            System.Threading.Thread.Sleep(retryDelayMs);
                        }
                    }

                    Logger.Warn($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Instrument '{ntName}' not available after {maxRetries * retryDelayMs}ms");
                    return null;
                }
                else
                {
                    // Instrument might already exist
                    Logger.Debug($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): CreateInstrument returned false, trying to get existing");
                    return Instrument.GetInstrument(symbol);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerService] CreateInstrumentFromMapping({symbol}): Exception - {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Subscribes to market data with proper closure for instrument name.
        /// Publishes prices to MarketDataReactiveHub for reactive consumption.
        /// </summary>
        private void SubscribeToIndicatorWithClosure(Instrument instrument, string instrumentName)
        {
            Logger.Debug($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Getting adapter");

            var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
            if (adapter != null)
            {
                Logger.Info($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Adapter found, calling SubscribeMarketData()");

                // Track the current close for this symbol to include in price updates
                double currentClose = 0;

                adapter.SubscribeMarketData(instrument, (type, price, vol, time, unk) => {
                    if (type == MarketDataType.Last)
                    {
                        Logger.Debug($"[MarketAnalyzerService] MarketData({instrumentName}): Last={price}");

                        // Publish to reactive hub (primary)
                        _hub.PublishIndexPrice(instrumentName, price, currentClose);

                        // Also update legacy logic for backward compatibility
                        _logic.UpdatePrice(instrumentName, price);
                    }
                    else if (type == MarketDataType.LastClose)
                    {
                        // LastClose is the prior day's close
                        Logger.Debug($"[MarketAnalyzerService] MarketData({instrumentName}): LastClose={price}");
                        currentClose = price;

                        // Publish to reactive hub
                        _hub.PublishIndexClose(instrumentName, price);

                        // Also update legacy logic for backward compatibility
                        _logic.UpdateClose(instrumentName, price);
                    }
                });

                Logger.Info($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Market data callback registered");

                // Request 1-minute historical data for 5 days
                RequestHistoricalData(instrument, instrumentName);
            }
            else
            {
                Logger.Error($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Adapter is NULL - cannot subscribe to market data");
            }
        }

        /// <summary>
        /// Uses Rx to await InstrumentDbReady before subscribing to NIFTY Futures.
        /// This ensures the NiftyFuturesSymbol is resolved from the database before we try to use it.
        /// </summary>
        private void SubscribeToNiftyFuturesWhenReady()
        {
            // First check if symbol is already resolved (in case InstrumentDbReady already fired)
            string niftyFutSymbol = MarketAnalyzerLogic.Instance.NiftyFuturesSymbol;
            if (!string.IsNullOrEmpty(niftyFutSymbol))
            {
                Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): Symbol already resolved: '{niftyFutSymbol}'");
                SubscribeToNiftyFutures(niftyFutSymbol);
                return;
            }

            // Symbol not resolved yet - wait for InstrumentDbReady via Rx with timeout
            Logger.Info("[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): Waiting for InstrumentDbReady (timeout=90s)...");

            _subscriptions.Add(
                _hub.InstrumentDbReadyStream
                    .Where(ready => ready)
                    .Timeout(TimeSpan.FromSeconds(90)) // Timeout to prevent infinite wait
                    .Take(1)
                    .Delay(TimeSpan.FromMilliseconds(500)) // Small delay to ensure symbol resolution completes
                    .Subscribe(
                        _ =>
                        {
                            string resolvedSymbol = MarketAnalyzerLogic.Instance.NiftyFuturesSymbol;
                            if (!string.IsNullOrEmpty(resolvedSymbol))
                            {
                                Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): InstrumentDbReady received, symbol resolved: '{resolvedSymbol}'");
                                SubscribeToNiftyFutures(resolvedSymbol);
                            }
                            else
                            {
                                Logger.Error("[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): InstrumentDbReady received but symbol still not resolved!");
                            }
                        },
                        ex =>
                        {
                            if (ex is TimeoutException)
                            {
                                Logger.Error("[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): Timeout waiting for InstrumentDbReady (90s) - NIFTY_I will not be available");
                            }
                            else
                            {
                                Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFuturesWhenReady(): Error - {ex.Message}", ex);
                            }
                        }));
        }

        /// <summary>
        /// Subscribes to NIFTY Futures (NIFTY_I) using the dynamically resolved symbol.
        /// Unlike indices which have pre-existing NT instruments, NIFTY Futures requires:
        /// 1. Adding the mapping to SymbolMappingService (with token from database)
        /// 2. Subscribing directly via MarketDataService using the token
        /// </summary>
        private void SubscribeToNiftyFutures(string niftyFutSymbol)
        {
            Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFutures(): Subscribing to '{niftyFutSymbol}'");

            Task.Run(async () => {
                try
                {
                    // Wait for adapter to be available (max 30 seconds)
                    Logger.Debug($"[MarketAnalyzerService] SubscribeToNiftyFutures({niftyFutSymbol}): Waiting for adapter connection...");
                    int maxWaitMs = 30000;
                    int waitedMs = 0;
                    while (waitedMs < maxWaitMs)
                    {
                        var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
                        if (adapter != null)
                        {
                            Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFutures({niftyFutSymbol}): Adapter connected after {waitedMs}ms");
                            break;
                        }
                        await Task.Delay(500);
                        waitedMs += 500;
                    }

                    if (waitedMs >= maxWaitMs)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFutures({niftyFutSymbol}): Timeout waiting for adapter connection");
                        return;
                    }

                    // Step 1: Add mapping to SymbolMappingService (like index_mappings.json does for GIFT_NIFTY)
                    // This ensures InstrumentManager.GetInstrumentToken() will find the token
                    bool mappingAdded = AddNiftyFuturesMappingToCache(niftyFutSymbol);
                    if (!mappingAdded)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFutures({niftyFutSymbol}): Failed to add mapping to cache");
                        return;
                    }

                    // Step 2: Subscribe to WebSocket using MarketDataService directly (no NT Instrument needed)
                    // This is similar to how indices work - we just need the token in the mapping cache
                    await SubscribeToNiftyFuturesViaWebSocket(niftyFutSymbol);

                    // Step 3: Request historical data for prior close
                    RequestNiftyFuturesHistoricalData(niftyFutSymbol);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFutures({niftyFutSymbol}): Exception occurred - {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Adds NIFTY Futures mapping to SymbolMappingService in-memory cache and persists to fo_mappings.json.
        /// This is similar to how index_mappings.json provides mappings for GIFT_NIFTY, NIFTY, SENSEX.
        /// </summary>
        private bool AddNiftyFuturesMappingToCache(string niftyFutSymbol)
        {
            try
            {
                // Get the token from MarketAnalyzerLogic (resolved from database)
                long token = _logic.NiftyFuturesToken;
                DateTime expiry = _logic.NiftyFuturesExpiry;

                if (token == 0)
                {
                    Logger.Warn($"[MarketAnalyzerService] AddNiftyFuturesMappingToCache({niftyFutSymbol}): Token not resolved");
                    return false;
                }

                Logger.Info($"[MarketAnalyzerService] AddNiftyFuturesMappingToCache({niftyFutSymbol}): token={token}, expiry={expiry:yyyy-MM-dd}");

                // Create mapping object (similar to MappedInstrument in index_mappings.json)
                var mapping = new MappedInstrument
                {
                    symbol = niftyFutSymbol,
                    zerodhaSymbol = niftyFutSymbol,
                    underlying = "NIFTY",
                    segment = "NFO-FUT",
                    instrument_token = token,
                    expiry = expiry,
                    tick_size = 0.05,
                    lot_size = 75, // NIFTY lot size
                    is_index = false
                };

                // Add to InstrumentManager (which adds to SymbolMappingService in-memory cache)
                InstrumentManager.Instance.AddMappedInstrument(niftyFutSymbol, mapping);

                // Also add with NIFTY_I alias for easy lookup
                InstrumentManager.Instance.AddMappedInstrument("NIFTY_I", mapping);

                Logger.Info($"[MarketAnalyzerService] AddNiftyFuturesMappingToCache({niftyFutSymbol}): Mapping added to cache (token={token})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerService] AddNiftyFuturesMappingToCache({niftyFutSymbol}): Exception - {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Subscribes to NIFTY Futures via WebSocket using MarketDataService.
        /// Uses SharedWebSocketService for efficient single-connection model.
        /// </summary>
        private async Task SubscribeToNiftyFuturesViaWebSocket(string niftyFutSymbol)
        {
            try
            {
                Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Starting WebSocket subscription");

                // Get the token from InstrumentManager (should now be in cache)
                long token = InstrumentManager.Instance.GetInstrumentToken(niftyFutSymbol);
                if (token == 0)
                {
                    Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Token not found in cache!");
                    return;
                }

                Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Token={token}, subscribing via SharedWebSocket");

                // Add the resolved symbol as an alias in the hub
                _hub.AddNiftyFuturesAlias(niftyFutSymbol);

                // Subscribe via MarketDataService (uses SharedWebSocketService)
                var marketDataService = MarketDataService.Instance;
                bool subscribed = await marketDataService.SubscribeToSymbolDirectAsync(
                    niftyFutSymbol,
                    (int)token,
                    isIndex: false,
                    onTick: (price, volume, timestamp) =>
                    {
                        Logger.Debug($"[MarketAnalyzerService] NIFTY_I Tick: price={price}");

                        // Publish to reactive hub
                        _hub.PublishIndexPrice(niftyFutSymbol, price);

                        // Also update legacy logic
                        _logic.UpdatePrice(niftyFutSymbol, price);
                    });

                if (subscribed)
                {
                    Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Successfully subscribed");

                    // Subscribe for NinjaTrader database persistence
                    // This registers a NT callback so ticks are saved to the database
                    SubscribeForPersistence(niftyFutSymbol);

                    // Also try with NIFTY_I alias for persistence
                    SubscribeForPersistence("NIFTY_I");
                }
                else
                {
                    Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Subscription failed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Requests historical data for NIFTY Futures to get prior close
        /// </summary>
        private void RequestNiftyFuturesHistoricalData(string niftyFutSymbol)
        {
            Task.Run(async () =>
            {
                try
                {
                    Logger.Info($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): Requesting 1min data for 5 days");
                    _logic.NotifyHistoricalDataStatus("NIFTY_I", "Requesting...");

                    // Calculate date range: 5 days back from now
                    DateTime toDate = DateTime.Now;
                    DateTime fromDate = toDate.AddDays(-5);

                    // Use HistoricalDataService to fetch data from Zerodha API
                    var historicalDataService = HistoricalDataService.Instance;
                    var records = await historicalDataService.GetHistoricalTrades(
                        BarsPeriodType.Minute,
                        niftyFutSymbol,  // Use the resolved symbol (e.g., NIFTY26JANFUT)
                        fromDate,
                        toDate,
                        MarketType.Futures,  // Use Futures market type
                        null);

                    if (records != null && records.Count > 0)
                    {
                        Logger.Info($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): Success - received {records.Count} bars");
                        _logic.NotifyHistoricalDataStatus("NIFTY_I", $"Done ({records.Count})");

                        // Extract last price and prior close
                        var sortedRecords = records.OrderBy(r => r.TimeStamp).ToList();
                        var lastRecord = sortedRecords.Last();
                        double lastPrice = lastRecord.Close;

                        // Find prior close - the close of the previous trading day
                        double priorClose = 0;
                        DateTime lastTradingDay = lastRecord.TimeStamp.Date;

                        var priorDayRecord = sortedRecords
                            .Where(r => r.TimeStamp.Date < lastTradingDay)
                            .OrderByDescending(r => r.TimeStamp)
                            .FirstOrDefault();

                        if (priorDayRecord != null)
                        {
                            priorClose = priorDayRecord.Close;
                            Logger.Info($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): PriorClose={priorClose} from {priorDayRecord.TimeStamp:yyyy-MM-dd HH:mm}");
                        }
                        else
                        {
                            Logger.Warn($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): Could not find prior day close");
                        }

                        Logger.Info($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): LastPrice={lastPrice}, PriorClose={priorClose}");

                        // Update both hub and legacy logic
                        if (priorClose > 0)
                        {
                            _hub.PublishIndexClose(niftyFutSymbol, priorClose);
                            _logic.UpdateClose(niftyFutSymbol, priorClose);
                        }

                        _hub.PublishIndexPrice(niftyFutSymbol, lastPrice, priorClose);
                        _logic.UpdatePrice(niftyFutSymbol, lastPrice);
                    }
                    else
                    {
                        Logger.Warn($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): No data received");
                        _logic.NotifyHistoricalDataStatus("NIFTY_I", "No Data");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] RequestNiftyFuturesHistoricalData({niftyFutSymbol}): Exception - {ex.Message}", ex);
                    _logic.NotifyHistoricalDataStatus("NIFTY_I", "Error");
                }
            });
        }

        /// <summary>
        /// Requests 1-minute historical data for 5 days for the given instrument
        /// Uses the HistoricalDataService to fetch data from Zerodha API
        /// </summary>
        private void RequestHistoricalData(Instrument instrument, string instrumentName)
        {
            Task.Run(async () =>
            {
                try
                {
                    Logger.Info($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): Requesting 1min data for 5 days");
                    _logic.NotifyHistoricalDataStatus(instrumentName, "Requesting...");

                    // Calculate date range: 5 days back from now
                    DateTime toDate = DateTime.Now;
                    DateTime fromDate = toDate.AddDays(-5);

                    // Use HistoricalDataService to fetch data from Zerodha API
                    var historicalDataService = HistoricalDataService.Instance;
                    var records = await historicalDataService.GetHistoricalTrades(
                        BarsPeriodType.Minute,
                        instrumentName,
                        fromDate,
                        toDate,
                        MarketType.Spot,  // MarketType is not used by the service, just logged
                        null);

                    if (records != null && records.Count > 0)
                    {
                        Logger.Info($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): Success - received {records.Count} bars");
                        _logic.NotifyHistoricalDataStatus(instrumentName, $"Done ({records.Count})");

                        // Extract last price (most recent bar's close) and prior close for change calculation
                        var sortedRecords = records.OrderBy(r => r.TimeStamp).ToList();
                        var lastRecord = sortedRecords.Last();
                        double lastPrice = lastRecord.Close;

                        // Find prior close - the close of the previous trading day
                        double priorClose = 0;
                        DateTime lastTradingDay = lastRecord.TimeStamp.Date;

                        // Find the last record from a previous day
                        var priorDayRecord = sortedRecords
                            .Where(r => r.TimeStamp.Date < lastTradingDay)
                            .OrderByDescending(r => r.TimeStamp)
                            .FirstOrDefault();

                        if (priorDayRecord != null)
                        {
                            priorClose = priorDayRecord.Close;
                            Logger.Info($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): PriorClose={priorClose} from {priorDayRecord.TimeStamp:yyyy-MM-dd HH:mm}");
                        }
                        else
                        {
                            Logger.Warn($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): Could not find prior day close");
                        }

                        Logger.Info($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): LastPrice={lastPrice} from {lastRecord.TimeStamp:yyyy-MM-dd HH:mm}, PriorClose={priorClose}");

                        // Update both hub and legacy logic with prior close first
                        if (priorClose > 0)
                        {
                            _hub.PublishIndexClose(instrumentName, priorClose);
                            _logic.UpdateClose(instrumentName, priorClose);
                        }

                        // Update with last price
                        _hub.PublishIndexPrice(instrumentName, lastPrice, priorClose);
                        _logic.UpdatePrice(instrumentName, lastPrice);
                    }
                    else
                    {
                        Logger.Warn($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): No data received");
                        _logic.NotifyHistoricalDataStatus(instrumentName, "No Data");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MarketAnalyzerService] RequestHistoricalData({instrumentName}): Exception - {ex.Message}", ex);
                    _logic.NotifyHistoricalDataStatus(instrumentName, "Error");
                }
            });
        }

        /// <summary>
        /// Callback when options are generated (via reactive stream from hub).
        /// </summary>
        private void OnOptionsGenerated(OptionsGeneratedEvent evt)
        {
            if (evt?.Options == null || evt.Options.Count == 0)
            {
                Logger.Warn("[MarketAnalyzerService] OnOptionsGenerated(): No options in event");
                return;
            }

            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Received {evt.Options.Count} options - {evt.SelectedUnderlying} {evt.SelectedExpiry:dd-MMM-yyyy} (DTE={evt.DTE})");

            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): First option - {evt.Options[0].underlying} {evt.Options[0].expiry:yyyy-MM-dd} {evt.Options[0].strike} {evt.Options[0].option_type}");

            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Queueing options for subscription...");
            _subscriptionManager.QueueSubscription(evt.Options);
            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Options queued successfully");
        }

        /// <summary>
        /// Subscribes to NinjaTrader market data for the given symbol to enable database persistence.
        /// This mirrors the OptionChainWindow.SubscribeForPersistence pattern.
        /// If the instrument doesn't exist in NinjaTrader, it will be created first.
        /// Even with an empty callback, NinjaTrader will persist ticks to its database.
        /// </summary>
        /// <param name="symbol">The NinjaTrader symbol name</param>
        private void SubscribeForPersistence(string symbol)
        {
            // Atomically try to reserve this symbol - if TryAdd returns false, another thread is handling it
            if (!_persistenceSubscribedSymbols.TryAdd(symbol, 0))
            {
                Logger.Debug($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Already subscribed for persistence");
                return;
            }

            try
            {
                // Must run on UI thread to access NinjaTrader instruments
                NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var nt = Instrument.GetInstrument(symbol);

                        // If instrument doesn't exist, create it first
                        if (nt == null)
                        {
                            Logger.Info($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Instrument not found, attempting to create...");

                            // Try to get mapping from InstrumentManager (for indices and F&O)
                            var mapping = InstrumentManager.Instance.GetMappingByNtSymbol(symbol);
                            if (mapping != null)
                            {
                                string ntName;
                                bool created = NinjaTraderHelper.CreateNTInstrumentFromMapping(mapping, out ntName);
                                if (created)
                                {
                                    Logger.Info($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Created NT instrument '{ntName}'");
                                    nt = Instrument.GetInstrument(ntName);
                                }
                            }
                            else
                            {
                                // Create a basic instrument definition for indices without mapping
                                var instrumentDef = new InstrumentDefinition
                                {
                                    Symbol = symbol,
                                    BrokerSymbol = symbol,
                                    Segment = "NSE",
                                    TickSize = 0.05,
                                    InstrumentToken = InstrumentManager.Instance.GetInstrumentToken(symbol)
                                };

                                string ntName;
                                bool created = InstrumentManager.Instance.CreateInstrument(instrumentDef, out ntName);
                                if (created)
                                {
                                    Logger.Info($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Created NT instrument '{ntName}'");
                                    nt = Instrument.GetInstrument(ntName);
                                }
                            }
                        }

                        if (nt == null)
                        {
                            Logger.Warn($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Failed to get/create instrument");
                            // Remove reservation since we failed
                            _persistenceSubscribedSymbols.TryRemove(symbol, out _);
                            return;
                        }

                        var adapter = Connector.Instance?.GetAdapter() as ZerodhaAdapter;
                        if (adapter == null)
                        {
                            Logger.Warn($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Adapter not available");
                            // Remove reservation since we failed
                            _persistenceSubscribedSymbols.TryRemove(symbol, out _);
                            return;
                        }

                        // Subscribe with empty callback - NinjaTrader will still persist ticks to database
                        adapter.SubscribeMarketData(nt, (t, p, v, time, a5) => { });
                        // Symbol already added via TryAdd at the start
                        Logger.Info($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Successfully subscribed for NT database persistence");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Exception in dispatcher - {ex.Message}", ex);
                        // Remove reservation since we failed
                        _persistenceSubscribedSymbols.TryRemove(symbol, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerService] SubscribeForPersistence({symbol}): Exception - {ex.Message}", ex);
            }
        }
    }
}
