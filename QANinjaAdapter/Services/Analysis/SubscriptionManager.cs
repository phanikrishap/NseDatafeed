using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using QABrokerAPI.Common.Enums;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Instruments;
using QANinjaAdapter.Services.MarketData;
using QANinjaAdapter.SyntheticInstruments;

namespace QANinjaAdapter.Services.Analysis
{
    /// <summary>
    /// Manages autonomous subscription workflow for generated option symbols.
    /// Handles: Registration -> NT Creation -> Live Subscription -> Historical Backfill
    /// </summary>
    public class SubscriptionManager
    {
        private static SubscriptionManager _instance;
        public static SubscriptionManager Instance => _instance ?? (_instance = new SubscriptionManager());

        private readonly ConcurrentQueue<MappedInstrument> _subscriptionQueue = new ConcurrentQueue<MappedInstrument>();
        private readonly ConcurrentQueue<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)> _historicalDataQueue = new ConcurrentQueue<(MappedInstrument, string, Instrument)>();
        private bool _isProcessing = false;
        private bool _isProcessingHistorical = false;
        private int _processedCount = 0;
        private int _totalQueued = 0;

        // Track processed instruments for BarsRequest after historical data is complete
        private readonly ConcurrentBag<(string ntSymbol, Instrument ntInstrument)> _processedInstruments = new ConcurrentBag<(string, Instrument)>();
        private readonly ConcurrentBag<string> _processedStraddleSymbols = new ConcurrentBag<string>();

        // BarsRequest management
        private readonly ConcurrentDictionary<string, BarsRequest> _activeBarsRequests = new ConcurrentDictionary<string, BarsRequest>();

        // Batching configuration for historical data requests
        // Zerodha allows ~3 requests/second for historical API, so 10 parallel with 4s delay = safe throughput
        private const int HISTORICAL_BATCH_SIZE = 10;
        private const int HISTORICAL_BATCH_DELAY_MS = 4000; // 4 seconds between batches

        // Optimized batch size for BarsRequest when data is already cached locally
        // Since no API calls needed, we can process much faster
        private const int CACHED_BATCH_SIZE = 25;
        private const int CACHED_BATCH_DELAY_MS = 500; // 500ms between batches for cached data

        // Event for UI updates
        public event Action<string, double> OptionPriceUpdated;

        // Event to notify when zerodhaSymbol is resolved from DB (generatedSymbol, zerodhaSymbol)
        public event Action<string, string> SymbolResolved;

        // Event to notify UI of historical data status (symbol, status like "Done (123)" or "No Data")
        public event Action<string, string> OptionStatusUpdated;

        private SubscriptionManager()
        {
            Logger.Info("[SubscriptionManager] Constructor: Initializing singleton instance");
        }

        /// <summary>
        /// Queues a list of instruments for subscription processing
        /// </summary>
        public void QueueSubscription(List<MappedInstrument> instruments)
        {
            Logger.Info($"[SubscriptionManager] QueueSubscription(): Received {instruments.Count} instruments to queue");

            _totalQueued = instruments.Count;
            _processedCount = 0;

            foreach (var inst in instruments)
            {
                _subscriptionQueue.Enqueue(inst);
                Logger.Debug($"[SubscriptionManager] QueueSubscription(): Enqueued {inst.symbol}");
            }

            Logger.Info($"[SubscriptionManager] QueueSubscription(): Queue size is now {_subscriptionQueue.Count}");

            if (!_isProcessing)
            {
                Logger.Info("[SubscriptionManager] QueueSubscription(): Starting queue processor...");
                Task.Run(ProcessQueue);
            }
            else
            {
                Logger.Info("[SubscriptionManager] QueueSubscription(): Queue processor already running");
            }
        }

        /// <summary>
        /// Processes the subscription queue one instrument at a time
        /// </summary>
        private async Task ProcessQueue()
        {
            Logger.Info("[SubscriptionManager] ProcessQueue(): Started processing");
            _isProcessing = true;

            while (_subscriptionQueue.TryDequeue(out var instrument))
            {
                _processedCount++;
                Logger.Info($"[SubscriptionManager] ProcessQueue(): Processing {_processedCount}/{_totalQueued} - {instrument.symbol}");

                try
                {
                    await SubscribeToInstrument(instrument);
                    Logger.Info($"[SubscriptionManager] ProcessQueue(): Completed {instrument.symbol}");

                    // Minimal delay - subscriptions are lightweight
                    await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SubscriptionManager] ProcessQueue(): Failed to process {instrument.symbol} - {ex.Message}", ex);
                }
            }

            _isProcessing = false;
            Logger.Info($"[SubscriptionManager] ProcessQueue(): Completed all {_processedCount} instruments");
        }

        /// <summary>
        /// Subscribes to a single instrument - full workflow
        /// </summary>
        private async Task SubscribeToInstrument(MappedInstrument instrument)
        {
            Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Starting subscription workflow");

            // Step 0: Look up instrument token from SQLite database by segment/underlying/expiry/strike/option_type
            if (instrument.instrument_token == 0 && instrument.expiry.HasValue && instrument.strike.HasValue && !string.IsNullOrEmpty(instrument.option_type))
            {
                Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Looking up option token in SQLite...");
                Logger.Debug($"[SubscriptionManager] Lookup params: segment={instrument.segment}, underlying={instrument.underlying}, expiry={instrument.expiry:yyyy-MM-dd}, strike={instrument.strike}, optionType={instrument.option_type}");

                var (token, tradingSymbol) = InstrumentManager.Instance.LookupOptionDetailsInSqlite(
                    instrument.segment,
                    instrument.underlying,
                    instrument.expiry.Value,
                    instrument.strike.Value,
                    instrument.option_type);

                if (token > 0)
                {
                    instrument.instrument_token = token;
                    if (!string.IsNullOrEmpty(tradingSymbol))
                    {
                        string generatedSymbol = instrument.symbol;
                        instrument.zerodhaSymbol = tradingSymbol;
                        Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Found token={token}, zerodhaSymbol={tradingSymbol}");

                        // Notify UI of symbol resolution so it can update its internal mapping
                        SymbolResolved?.Invoke(generatedSymbol, tradingSymbol);
                    }
                }
                else
                {
                    Logger.Warn($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Option token not found in SQLite - cannot subscribe");
                    return;
                }
            }

            // Step 1: Register in Instrument Manager (Memory + JSON)
            Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Step 1 - Adding to InstrumentManager");
            InstrumentManager.Instance.AddMappedInstrument(instrument);
            Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Registered in InstrumentManager");

            // Step 2: Create NT MasterInstrument
            Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Step 2 - Creating InstrumentDefinition");
            var instrumentDef = new InstrumentDefinition
            {
                Symbol = instrument.zerodhaSymbol ?? instrument.symbol, // Use zerodhaSymbol (correct format) as NT symbol
                BrokerSymbol = instrument.zerodhaSymbol ?? instrument.symbol,
                Segment = instrument.segment?.Contains("BFO") == true ? "BSE" : "NSE",
                MarketType = MarketType.UsdM // Options are UsdM (F&O segment)
            };

            string ntName = "";
            bool created = false;

            Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Step 3 - Creating NT instrument on UI thread");

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Inside dispatcher - calling CreateInstrument");
                    created = InstrumentManager.Instance.CreateInstrument(instrumentDef, out ntName);
                    Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): CreateInstrument returned created={created}, ntName='{ntName}'");
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): CreateInstrument exception - {ex.Message}", ex);
                return;
            }

            if (created || !string.IsNullOrEmpty(ntName))
            {
                Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): NT Instrument created/found as '{ntName}'");

                // Step 4: Get NT Instrument handle
                Instrument ntInstrument = null;

                try
                {
                    await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                    {
                        Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({ntName}): Getting Instrument handle");
                        ntInstrument = Instrument.GetInstrument(ntName);
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"[SubscriptionManager] SubscribeToInstrument({ntName}): GetInstrument exception - {ex.Message}", ex);
                    return;
                }

                if (ntInstrument != null)
                {
                    Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Got NT Instrument handle successfully");

                    // Step 5: Live Subscription
                    Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({ntName}): Step 5 - Setting up live subscription");
                    var adapter = Connector.Instance.GetAdapter() as QAAdapter;

                    if (adapter != null)
                    {
                        Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Adapter found, calling SubscribeMarketData");
                        string symbolForClosure = ntName;

                        adapter.SubscribeMarketData(ntInstrument, (type, price, size, time, unknown) =>
                        {
                            // Update UI with live prices
                            if (type == MarketDataType.Last && price > 0)
                            {
                                // DEBUG: Log to confirm callback is firing (sample to reduce log spam)
                                if (symbolForClosure.Contains("85500") && DateTime.Now.Second % 5 == 0)
                                {
                                    Logger.Debug($"[SubscriptionManager] LiveData CALLBACK FIRING: {symbolForClosure} = {price}");
                                }
                                OptionPriceUpdated?.Invoke(symbolForClosure, price);
                            }
                        });

                        Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Live subscription active");
                    }
                    else
                    {
                        Logger.Warn($"[SubscriptionManager] SubscribeToInstrument({ntName}): Adapter is NULL - cannot subscribe to live data");
                    }

                    // Step 6: Queue for batched historical data fetch (don't trigger immediately)
                    Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Queueing for batched historical data fetch");
                    _historicalDataQueue.Enqueue((instrument, ntName, ntInstrument));

                    // Start historical data processor if not already running
                    if (!_isProcessingHistorical)
                    {
                        Logger.Info("[SubscriptionManager] Starting historical data batch processor...");
                        _ = Task.Run(ProcessHistoricalDataQueue);
                    }
                }
                else
                {
                    Logger.Error($"[SubscriptionManager] SubscribeToInstrument({ntName}): Instrument.GetInstrument() returned NULL");
                }
            }
            else
            {
                Logger.Error($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): CreateInstrument failed - created={created}, ntName='{ntName}'");
            }
        }

        /// <summary>
        /// Processes the historical data queue in batches to avoid overloading Zerodha API
        /// </summary>
        private async Task ProcessHistoricalDataQueue()
        {
            Logger.Info("[SubscriptionManager] ProcessHistoricalDataQueue(): Started batch processor");
            _isProcessingHistorical = true;

            int batchNumber = 0;
            int totalProcessed = 0;

            while (!_historicalDataQueue.IsEmpty || _isProcessing)
            {
                // Wait for subscription processing to complete first before starting historical fetch
                if (_isProcessing)
                {
                    Logger.Debug("[SubscriptionManager] ProcessHistoricalDataQueue(): Waiting for subscription processing to complete...");
                    await Task.Delay(1000);
                    continue;
                }

                // Collect a batch of instruments
                var batch = new List<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)>();
                while (batch.Count < HISTORICAL_BATCH_SIZE && _historicalDataQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    // No more items, exit
                    break;
                }

                batchNumber++;
                Logger.Info($"[SubscriptionManager] ProcessHistoricalDataQueue(): Processing batch {batchNumber} with {batch.Count} instruments");

                // Process the batch in parallel
                var tasks = batch.Select(item => TriggerBackfillAndUpdatePrice(item.mappedInst, item.ntSymbol, item.ntInstrument)).ToArray();
                await Task.WhenAll(tasks);

                totalProcessed += batch.Count;
                Logger.Info($"[SubscriptionManager] ProcessHistoricalDataQueue(): Batch {batchNumber} complete. Total processed: {totalProcessed}");

                // Wait between batches to avoid rate limiting
                if (!_historicalDataQueue.IsEmpty)
                {
                    Logger.Info($"[SubscriptionManager] ProcessHistoricalDataQueue(): Waiting {HISTORICAL_BATCH_DELAY_MS / 1000}s before next batch...");
                    await Task.Delay(HISTORICAL_BATCH_DELAY_MS);
                }
            }

            _isProcessingHistorical = false;
            Logger.Info($"[SubscriptionManager] ProcessHistoricalDataQueue(): Completed all batches. Total instruments processed: {totalProcessed}");

            // After all historical data is fetched and cached, trigger BarsRequest to push to NinjaTrader DB
            Logger.Info("[SubscriptionManager] ProcessHistoricalDataQueue(): Starting BarsRequest to push data to NinjaTrader...");
            await TriggerBarsRequestForAllInstruments();
        }

        /// <summary>
        /// Triggers historical data backfill for an instrument and updates the price in UI
        /// </summary>
        private async Task TriggerBackfillAndUpdatePrice(MappedInstrument mappedInst, string ntSymbol, Instrument instrument)
        {
            // Use zerodhaSymbol for API calls (the correct format from DB), ntSymbol for UI updates
            string apiSymbol = mappedInst.zerodhaSymbol ?? ntSymbol;
            Logger.Info($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Starting 3-day backfill (apiSymbol={apiSymbol})");

            try
            {
                DateTime end = DateTime.Now;
                DateTime start = end.AddDays(-3);

                Logger.Info($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Date range {start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}");

                // Request 1-Minute Data via HistoricalDataService using the zerodha symbol
                Logger.Debug($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Requesting 1-Minute historical data for {apiSymbol}...");

                var historicalDataService = HistoricalDataService.Instance;
                var records = await historicalDataService.GetHistoricalTrades(
                    BarsPeriodType.Minute,
                    apiSymbol,  // Use the zerodha symbol for API lookup
                    start,
                    end,
                    MarketType.UsdM,
                    null
                );

                if (records != null && records.Count > 0)
                {
                    Logger.Info($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Received {records.Count} bars");

                    // Notify UI of status - "Cached" means data is in SQLite cache, ready for BarsRequest
                    // "Done" will be shown after BarsRequest completes and data is in NinjaTrader DB
                    OptionStatusUpdated?.Invoke(ntSymbol, $"Cached ({records.Count})");

                    // Track this instrument for BarsRequest later
                    _processedInstruments.Add((ntSymbol, instrument));

                    // Cache bars for straddle computation if this is an option
                    if (!string.IsNullOrEmpty(mappedInst.option_type) && mappedInst.strike.HasValue)
                    {
                        string straddleSymbol = CacheOptionBarsForStraddle(mappedInst, apiSymbol, records);
                        if (!string.IsNullOrEmpty(straddleSymbol))
                        {
                            _processedStraddleSymbols.Add(straddleSymbol);
                        }
                    }

                    // Extract last price and update UI
                    var lastRecord = records.OrderByDescending(r => r.TimeStamp).FirstOrDefault();
                    if (lastRecord != null && lastRecord.Close > 0)
                    {
                        Logger.Info($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): LastPrice={lastRecord.Close} from {lastRecord.TimeStamp:yyyy-MM-dd HH:mm}");
                        OptionPriceUpdated?.Invoke(ntSymbol, lastRecord.Close);
                    }
                }
                else
                {
                    Logger.Warn($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): No historical data received for {apiSymbol}");
                    OptionStatusUpdated?.Invoke(ntSymbol, "No Data");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Exception occurred - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Caches option bar data for straddle computation.
        /// When both CE and PE bars are cached, StraddleBarCache combines them into STRDL bars.
        /// Returns the straddle symbol for tracking.
        /// </summary>
        private string CacheOptionBarsForStraddle(MappedInstrument mappedInst, string apiSymbol, List<Classes.Record> records)
        {
            try
            {
                // Build straddle symbol: {underlying}{expiry}{strike}_STRDL
                // e.g., SENSEX25DEC85000_STRDL
                string expiryStr = mappedInst.expiry?.ToString("yyMMM").ToUpper() ?? "";
                string strikeStr = mappedInst.strike?.ToString("0") ?? "";
                string straddleSymbol = $"{mappedInst.underlying}{expiryStr}{strikeStr}_STRDL";

                Logger.Info($"[SubscriptionManager] CacheOptionBarsForStraddle: {apiSymbol} -> {straddleSymbol} ({mappedInst.option_type})");

                if (mappedInst.option_type?.ToUpper() == "CE")
                {
                    StraddleBarCache.Instance.StoreCEBars(straddleSymbol, apiSymbol, records);
                }
                else if (mappedInst.option_type?.ToUpper() == "PE")
                {
                    StraddleBarCache.Instance.StorePEBars(straddleSymbol, apiSymbol, records);
                }

                return straddleSymbol;
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] CacheOptionBarsForStraddle: Error caching bars - {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Triggers BarsRequest for all processed instruments to push data into NinjaTrader's database.
        /// Called after all historical data has been fetched and cached.
        /// OPTIMIZED: Uses larger batch size (25) and shorter delays (500ms) when data is cached locally.
        /// </summary>
        private async Task TriggerBarsRequestForAllInstruments()
        {
            Logger.Info($"[SubscriptionManager] TriggerBarsRequestForAllInstruments: Starting - {_processedInstruments.Count} CE/PE instruments, {_processedStraddleSymbols.Distinct().Count()} STRDL symbols");

            DateTime fromDate = DateTime.Now.AddDays(-3);
            DateTime toDate = DateTime.Now;

            int batchCount = 0;
            int totalRequested = 0;

            // Separate CE/PE instruments into cached and non-cached for optimized batching
            var cepeInstruments = _processedInstruments.ToList();
            var cachedCEPE = new List<(string ntSymbol, Instrument ntInstrument)>();
            var nonCachedCEPE = new List<(string ntSymbol, Instrument ntInstrument)>();

            foreach (var (ntSymbol, ntInstrument) in cepeInstruments)
            {
                if (HistoricalBarCache.Instance.HasCachedData(ntSymbol, "minute", fromDate, toDate))
                    cachedCEPE.Add((ntSymbol, ntInstrument));
                else
                    nonCachedCEPE.Add((ntSymbol, ntInstrument));
            }

            Logger.Info($"[SubscriptionManager] TriggerBarsRequestForAllInstruments: CE/PE split - {cachedCEPE.Count} cached, {nonCachedCEPE.Count} non-cached");

            // Process CACHED CE/PE instruments with larger batch size and shorter delay
            if (cachedCEPE.Count > 0)
            {
                Logger.Info($"[SubscriptionManager] Processing {cachedCEPE.Count} CACHED CE/PE instruments (batch size={CACHED_BATCH_SIZE}, delay={CACHED_BATCH_DELAY_MS}ms)");
                for (int i = 0; i < cachedCEPE.Count; i += CACHED_BATCH_SIZE)
                {
                    var batch = cachedCEPE.Skip(i).Take(CACHED_BATCH_SIZE).ToList();
                    batchCount++;

                    Logger.Info($"[SubscriptionManager] Processing CACHED CE/PE batch {batchCount} with {batch.Count} instruments");

                    foreach (var (ntSymbol, ntInstrument) in batch)
                    {
                        await RequestBarsForInstrument(ntSymbol, ntInstrument, fromDate, toDate);
                        totalRequested++;
                    }

                    // Short delay between cached batches
                    if (i + CACHED_BATCH_SIZE < cachedCEPE.Count)
                    {
                        await Task.Delay(CACHED_BATCH_DELAY_MS);
                    }
                }
            }

            // Process NON-CACHED CE/PE instruments with standard batch size
            if (nonCachedCEPE.Count > 0)
            {
                Logger.Info($"[SubscriptionManager] Processing {nonCachedCEPE.Count} NON-CACHED CE/PE instruments (batch size={HISTORICAL_BATCH_SIZE}, delay=1000ms)");
                for (int i = 0; i < nonCachedCEPE.Count; i += HISTORICAL_BATCH_SIZE)
                {
                    var batch = nonCachedCEPE.Skip(i).Take(HISTORICAL_BATCH_SIZE).ToList();
                    batchCount++;

                    Logger.Info($"[SubscriptionManager] Processing NON-CACHED CE/PE batch {batchCount} with {batch.Count} instruments");

                    foreach (var (ntSymbol, ntInstrument) in batch)
                    {
                        await RequestBarsForInstrument(ntSymbol, ntInstrument, fromDate, toDate);
                        totalRequested++;
                    }

                    // Standard delay between non-cached batches
                    if (i + HISTORICAL_BATCH_SIZE < nonCachedCEPE.Count)
                    {
                        await Task.Delay(1000);
                    }
                }
            }

            // Process STRDL instruments - separate cached vs non-cached
            var straddleSymbols = _processedStraddleSymbols.Distinct().ToList();
            var cachedStraddles = straddleSymbols.Where(s => StraddleBarCache.Instance.HasCachedData(s)).ToList();
            var nonCachedStraddles = straddleSymbols.Where(s => !StraddleBarCache.Instance.HasCachedData(s)).ToList();

            Logger.Info($"[SubscriptionManager] TriggerBarsRequestForAllInstruments: STRDL split - {cachedStraddles.Count} cached, {nonCachedStraddles.Count} non-cached");

            // Process CACHED STRDL instruments with larger batch size
            if (cachedStraddles.Count > 0)
            {
                Logger.Info($"[SubscriptionManager] Processing {cachedStraddles.Count} CACHED STRDL instruments (batch size={CACHED_BATCH_SIZE}, delay={CACHED_BATCH_DELAY_MS}ms)");
                for (int i = 0; i < cachedStraddles.Count; i += CACHED_BATCH_SIZE)
                {
                    var batch = cachedStraddles.Skip(i).Take(CACHED_BATCH_SIZE).ToList();
                    batchCount++;

                    Logger.Info($"[SubscriptionManager] Processing CACHED STRDL batch {batchCount} with {batch.Count} instruments");

                    foreach (var straddleSymbol in batch)
                    {
                        await RequestBarsForStraddleSymbol(straddleSymbol, fromDate, toDate);
                        totalRequested++;
                    }

                    // Short delay between cached batches
                    if (i + CACHED_BATCH_SIZE < cachedStraddles.Count)
                    {
                        await Task.Delay(CACHED_BATCH_DELAY_MS);
                    }
                }
            }

            // Process NON-CACHED STRDL instruments with standard batch size
            if (nonCachedStraddles.Count > 0)
            {
                Logger.Info($"[SubscriptionManager] Processing {nonCachedStraddles.Count} NON-CACHED STRDL instruments (batch size={HISTORICAL_BATCH_SIZE}, delay=1000ms)");
                for (int i = 0; i < nonCachedStraddles.Count; i += HISTORICAL_BATCH_SIZE)
                {
                    var batch = nonCachedStraddles.Skip(i).Take(HISTORICAL_BATCH_SIZE).ToList();
                    batchCount++;

                    Logger.Info($"[SubscriptionManager] Processing NON-CACHED STRDL batch {batchCount} with {batch.Count} instruments");

                    foreach (var straddleSymbol in batch)
                    {
                        await RequestBarsForStraddleSymbol(straddleSymbol, fromDate, toDate);
                        totalRequested++;
                    }

                    // Standard delay between non-cached batches
                    if (i + HISTORICAL_BATCH_SIZE < nonCachedStraddles.Count)
                    {
                        await Task.Delay(1000);
                    }
                }
            }

            Logger.Info($"[SubscriptionManager] TriggerBarsRequestForAllInstruments: Completed - {totalRequested} total BarsRequests sent");
        }

        /// <summary>
        /// Requests bars for a CE/PE instrument to save to NinjaTrader database.
        /// Optimized: Checks if data is already cached - if so, uses smaller BarsRequest
        /// which will load quickly from NinjaTrader's local database.
        /// </summary>
        private async Task RequestBarsForInstrument(string ntSymbol, Instrument ntInstrument, DateTime fromDate, DateTime toDate)
        {
            try
            {
                // Check if we have cached data - if so, NinjaTrader likely has it too
                bool hasCachedData = HistoricalBarCache.Instance.HasCachedData(ntSymbol, "minute", fromDate, toDate);

                // If cached, use smaller barsBack (just to trigger NT to load from its local DB)
                // If not cached, use full 1500 bars to fetch from provider
                int barsBack = hasCachedData ? 100 : 1500;

                if (hasCachedData)
                {
                    Logger.Info($"[SubscriptionManager] RequestBarsForInstrument({ntSymbol}): Cache HIT - using optimized BarsRequest with {barsBack} bars");
                }
                else
                {
                    Logger.Info($"[SubscriptionManager] RequestBarsForInstrument({ntSymbol}): Cache MISS - using full BarsRequest with {barsBack} bars");
                }

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // BarsRequest constructor: (Instrument, int barsBack)
                        // If cached: small request that loads quickly from NT's local database
                        // If not cached: full request to cover 3 days of 1-minute data (~375 bars/day * 3 = ~1125)
                        var barsRequest = new BarsRequest(ntInstrument, barsBack);
                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Minute,
                            Value = 1
                        };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.Update += OnBarsRequestUpdate;

                        string symbolForClosure = ntSymbol; // Capture for closure
                        _activeBarsRequests.TryAdd(ntSymbol, barsRequest);
                        Instrument instrumentForClosure = ntInstrument; // Capture for closure
                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            int barCount = request.Bars?.Count ?? 0;
                            if (errorCode == ErrorCode.NoError)
                            {
                                Logger.Info($"[SubscriptionManager] BarsRequest completed for {symbolForClosure}: {barCount} bars inserted to NT DB");

                                // LOG SUBSCRIPTION STATE
                                var refDetails = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Info($"[SubscriptionManager] PRE-DONE STATUS: {symbolForClosure} - RefCount={refDetails.Count}, Sticky={refDetails.IsSticky}, Consumers={string.Join(",", refDetails.Consumers)}");

                                // Update UI status to "Done" now that data is in NinjaTrader database
                                OptionStatusUpdated?.Invoke(symbolForClosure, $"Done ({barCount})");

                                // LOG SUBSCRIPTION STATE AGAIN
                                var refDetailsAfter = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Info($"[SubscriptionManager] POST-DONE STATUS: {symbolForClosure} - RefCount={refDetailsAfter.Count}, Sticky={refDetailsAfter.IsSticky}, Consumers={string.Join(",", refDetailsAfter.Consumers)}");

                                // Start VWAP calculation for this instrument using hidden BarsRequest
                                _ = VWAPCalculatorService.Instance.StartVWAPCalculation(symbolForClosure, instrumentForClosure);
                            }
                            else
                            {
                                Logger.Warn($"[SubscriptionManager] BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                                OptionStatusUpdated?.Invoke(symbolForClosure, $"Error: {errorCode}");
                            }

                            // IMPORTANT: Do NOT dispose BarsRequest - keep it alive for real-time tick storage
                            // When BarsRequest is active with Update handler, NinjaTrader writes incoming ticks to its database.
                            // Disposing it stops real-time tick storage (only historical bars would be saved).
                            // We keep the BarsRequest in _activeBarsRequests to maintain the live subscription.
                            Logger.Info($"[SubscriptionManager] BarsRequest for {symbolForClosure} completed - keeping alive for real-time tick storage");
                        });

                        Logger.Debug($"[SubscriptionManager] BarsRequest sent for {ntSymbol}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] RequestBarsForInstrument({ntSymbol}): Error - {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] RequestBarsForInstrument({ntSymbol}): Dispatcher error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Requests bars for a STRDL synthetic instrument to save to NinjaTrader database.
        /// Optimized: For STRDL instruments, we check StraddleBarCache to see if data is ready.
        /// </summary>
        private async Task RequestBarsForStraddleSymbol(string straddleSymbol, DateTime fromDate, DateTime toDate)
        {
            try
            {
                // Check if straddle bars are already computed and cached
                bool hasStraddleData = StraddleBarCache.Instance.HasCachedData(straddleSymbol);

                // If straddle data is cached, NinjaTrader likely has it too - use smaller request
                int barsBack = hasStraddleData ? 100 : 1500;

                if (hasStraddleData)
                {
                    Logger.Info($"[SubscriptionManager] RequestBarsForStraddleSymbol({straddleSymbol}): Cache HIT - using optimized BarsRequest with {barsBack} bars");
                }
                else
                {
                    Logger.Info($"[SubscriptionManager] RequestBarsForStraddleSymbol({straddleSymbol}): Cache MISS - using full BarsRequest with {barsBack} bars");
                }

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Get or create the STRDL instrument
                        var ntInstrument = Instrument.GetInstrument(straddleSymbol);
                        if (ntInstrument == null)
                        {
                            Logger.Warn($"[SubscriptionManager] RequestBarsForStraddleSymbol({straddleSymbol}): Instrument not found, skipping");
                            return;
                        }

                        // BarsRequest constructor: (Instrument, int barsBack)
                        // If cached: small request that loads quickly from NT's local database
                        // If not cached: full request to fetch all data
                        var barsRequest = new BarsRequest(ntInstrument, barsBack);
                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Minute,
                            Value = 1
                        };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.Update += OnBarsRequestUpdate;

                        string symbolForClosure = straddleSymbol; // Capture for closure
                        Instrument instrumentForClosure = ntInstrument; // Capture for closure
                        _activeBarsRequests.TryAdd(straddleSymbol, barsRequest);
                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            int barCount = request.Bars?.Count ?? 0;
                            if (errorCode == ErrorCode.NoError)
                            {
                                Logger.Info($"[SubscriptionManager] STRDL BarsRequest completed for {symbolForClosure}: {barCount} bars inserted to NT DB");

                                // LOG SUBSCRIPTION STATE
                                var refDetails = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Info($"[SubscriptionManager] PRE-DONE STATUS (STRDL): {symbolForClosure} - RefCount={refDetails.Count}, Sticky={refDetails.IsSticky}, Consumers={string.Join(",", refDetails.Consumers)}");

                                // Update UI status to "Done" now that data is in NinjaTrader database
                                OptionStatusUpdated?.Invoke(symbolForClosure, $"Done ({barCount})");

                                // LOG SUBSCRIPTION STATE AGAIN
                                var refDetailsAfter = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Info($"[SubscriptionManager] POST-DONE STATUS (STRDL): {symbolForClosure} - RefCount={refDetailsAfter.Count}, Sticky={refDetailsAfter.IsSticky}, Consumers={string.Join(",", refDetailsAfter.Consumers)}");

                                // Start VWAP calculation for this STRDL instrument using hidden BarsRequest
                                _ = VWAPCalculatorService.Instance.StartVWAPCalculation(symbolForClosure, instrumentForClosure);
                            }
                            else
                            {
                                Logger.Warn($"[SubscriptionManager] STRDL BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                                OptionStatusUpdated?.Invoke(symbolForClosure, $"Error: {errorCode}");
                            }

                            // IMPORTANT: Do NOT dispose BarsRequest - keep it alive for real-time tick storage
                            // When BarsRequest is active with Update handler, NinjaTrader writes incoming ticks to its database.
                            // Disposing it stops real-time tick storage (only historical bars would be saved).
                            // We keep the BarsRequest in _activeBarsRequests to maintain the live subscription.
                            Logger.Info($"[SubscriptionManager] STRDL BarsRequest for {symbolForClosure} completed - keeping alive for real-time tick storage");
                        });

                        Logger.Debug($"[SubscriptionManager] STRDL BarsRequest sent for {straddleSymbol}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] RequestBarsForStraddleSymbol({straddleSymbol}): Error - {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] RequestBarsForStraddleSymbol({straddleSymbol}): Dispatcher error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Event handler for BarsRequest updates
        /// </summary>
        private void OnBarsRequestUpdate(object sender, BarsUpdateEventArgs e)
        {
            // Data is automatically cached by NinjaTrader
            // e.BarsSeries provides the bars data
            Logger.Debug($"[SubscriptionManager] BarsRequest update: MinIndex={e.MinIndex}, MaxIndex={e.MaxIndex}");
        }
    }
}
