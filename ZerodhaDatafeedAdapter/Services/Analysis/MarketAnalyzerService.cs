using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Models;
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
        private bool _isRunning = false;

        private MarketAnalyzerService()
        {
            _logic = MarketAnalyzerLogic.Instance;
            _subscriptionManager = SubscriptionManager.Instance;

            Logger.Info("[MarketAnalyzerService] Constructor: Initializing singleton instance");

            // Wire up events
            _logic.OptionsGenerated += OnOptionsGenerated;
            _logic.StatusUpdated += msg => Logger.Debug($"[MarketAnalyzerService] StatusUpdate: {msg}");

            Logger.Info("[MarketAnalyzerService] Constructor: Events wired up successfully");
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

            // Subscribe to indicator symbols
            SubscribeToIndicator("GIFT_NIFTY");
            SubscribeToIndicator("NIFTY");
            SubscribeToIndicator("SENSEX");

            // Subscribe to NIFTY Futures (NIFTY_I) - uses dynamically resolved symbol (e.g., NIFTY26JANFUT)
            string niftyFutSymbol = MarketAnalyzerLogic.Instance.NiftyFuturesSymbol;
            if (!string.IsNullOrEmpty(niftyFutSymbol))
            {
                Logger.Info($"[MarketAnalyzerService] Start(): Subscribing to NIFTY_I via resolved symbol '{niftyFutSymbol}'");
                SubscribeToNiftyFutures(niftyFutSymbol);
            }
            else
            {
                Logger.Warn("[MarketAnalyzerService] Start(): NIFTY Futures symbol not resolved yet, will retry");
                // Retry after a delay
                Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    string resolvedSymbol = MarketAnalyzerLogic.Instance.NiftyFuturesSymbol;
                    if (!string.IsNullOrEmpty(resolvedSymbol))
                    {
                        Logger.Info($"[MarketAnalyzerService] Start(): Retry - subscribing to NIFTY_I via '{resolvedSymbol}'");
                        SubscribeToNiftyFutures(resolvedSymbol);
                    }
                    else
                    {
                        Logger.Error("[MarketAnalyzerService] Start(): NIFTY Futures symbol still not resolved after retry");
                    }
                });
            }

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
                    return Instrument.GetInstrument(ntName);
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
        /// Subscribes to market data with proper closure for instrument name
        /// </summary>
        private void SubscribeToIndicatorWithClosure(Instrument instrument, string instrumentName)
        {
            Logger.Debug($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Getting adapter");

            var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
            if (adapter != null)
            {
                Logger.Info($"[MarketAnalyzerService] SubscribeToIndicatorWithClosure({instrumentName}): Adapter found, calling SubscribeMarketData()");

                adapter.SubscribeMarketData(instrument, (type, price, vol, time, unk) => {
                    if (type == MarketDataType.Last)
                    {
                        Logger.Debug($"[MarketAnalyzerService] MarketData({instrumentName}): Last={price}");
                        _logic.UpdatePrice(instrumentName, price);
                    }
                    else if (type == MarketDataType.LastClose)
                    {
                        // LastClose is the prior day's close - update the Close property, NOT the current price
                        Logger.Debug($"[MarketAnalyzerService] MarketData({instrumentName}): LastClose={price}");
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

                // Subscribe via MarketDataService (uses SharedWebSocketService)
                var marketDataService = MarketDataService.Instance;
                bool subscribed = await marketDataService.SubscribeToSymbolDirectAsync(
                    niftyFutSymbol,
                    (int)token,
                    isIndex: false,
                    onTick: (price, volume, timestamp) =>
                    {
                        Logger.Debug($"[MarketAnalyzerService] NIFTY_I Tick: price={price}");
                        _logic.UpdatePrice(niftyFutSymbol, price);
                    });

                if (subscribed)
                {
                    Logger.Info($"[MarketAnalyzerService] SubscribeToNiftyFuturesViaWebSocket({niftyFutSymbol}): Successfully subscribed");
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

                        // Update the logic - use niftyFutSymbol which routes to NiftyFuturesTicker
                        if (priorClose > 0)
                        {
                            _logic.UpdateClose(niftyFutSymbol, priorClose);
                        }

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

                        // Update the logic with prior close first (so it's available when price update triggers calculations)
                        if (priorClose > 0)
                        {
                            _logic.UpdateClose(instrumentName, priorClose);
                        }

                        // Update the logic with last price
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
        /// Callback when options are generated by MarketAnalyzerLogic
        /// </summary>
        private void OnOptionsGenerated(List<MappedInstrument> options)
        {
            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Received {options.Count} options from logic engine");

            if (options.Count > 0)
            {
                Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): First option - {options[0].underlying} {options[0].expiry:yyyy-MM-dd} {options[0].strike} {options[0].option_type}");
            }

            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Queueing options for subscription...");
            _subscriptionManager.QueueSubscription(options);
            Logger.Info($"[MarketAnalyzerService] OnOptionsGenerated(): Options queued successfully");
        }
    }
}
