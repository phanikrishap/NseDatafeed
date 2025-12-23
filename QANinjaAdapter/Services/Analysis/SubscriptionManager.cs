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

        // Batching configuration for historical data requests
        // Zerodha allows ~3 requests/second for historical API, so 10 parallel with 4s delay = safe throughput
        private const int HISTORICAL_BATCH_SIZE = 10;
        private const int HISTORICAL_BATCH_DELAY_MS = 4000; // 4 seconds between batches

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
                                Logger.Debug($"[SubscriptionManager] LiveData({symbolForClosure}): Last={price}");
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

                    // Notify UI of status with bar count (like indices show "Done (2713)")
                    OptionStatusUpdated?.Invoke(ntSymbol, $"Done ({records.Count})");

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
    }
}
