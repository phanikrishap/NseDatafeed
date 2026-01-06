using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.SyntheticInstruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Manages autonomous subscription workflow for generated option symbols.
    /// Handles: Registration -> NT Creation -> Live Subscription -> Historical Backfill
    ///
    /// Architecture: TPL Dataflow Pipeline (event-driven, backpressure-aware)
    /// ┌─────────────────┐     ┌──────────────────┐     ┌───────────────────┐
    /// │ _subscriptionBlock │ --> │ _historicalBlock │ --> │ _streamingBlock │
    /// │ (BoundedCapacity) │     │ (Rate Limited)   │     │ (UI Thread Safe) │
    /// └─────────────────┘     └──────────────────┘     └───────────────────┘
    ///
    /// Benefits over ConcurrentQueue + Task.Run:
    /// - Built-in backpressure (BoundedCapacity prevents memory overflow)
    /// - No polling loops (event-driven completion)
    /// - Proper async/await flow (no fire-and-forget Task.Run)
    /// - Automatic batching with MaxDegreeOfParallelism
    /// - Clean shutdown via Complete() + Completion task
    /// </summary>
    public class SubscriptionManager
    {
        private static SubscriptionManager _instance;
        public static SubscriptionManager Instance => _instance ?? (_instance = new SubscriptionManager());

        // ═══════════════════════════════════════════════════════════════════
        // TPL DATAFLOW PIPELINE BLOCKS
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Stage 1: Subscription processing - creates NT instruments and sets up WebSocket subscriptions.
        /// MaxDegreeOfParallelism=1 ensures sequential processing (required for NinjaTrader UI thread safety).
        /// BoundedCapacity=500 provides backpressure if subscriptions come faster than we can process.
        /// </summary>
        private ActionBlock<MappedInstrument> _subscriptionBlock;

        /// <summary>
        /// Stage 2: Historical data fetching - caches data from Zerodha API.
        /// MaxDegreeOfParallelism=10 allows parallel API calls within rate limits.
        /// BoundedCapacity=200 prevents memory overflow during large batches.
        /// </summary>
        private ActionBlock<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)> _historicalBlock;

        /// <summary>
        /// Stage 3: Streaming setup - triggers BarsRequest and VWAP calculation.
        /// MaxDegreeOfParallelism=6 balances throughput with NinjaTrader resource limits.
        /// BoundedCapacity=100 provides backpressure from historical processing.
        /// </summary>
        private ActionBlock<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)> _streamingBlock;

        /// <summary>
        /// Signals when all subscription processing is complete for a batch.
        /// Used by callers to await completion instead of polling.
        /// </summary>
        private TaskCompletionSource<bool> _batchCompletionSource;

        // Pipeline coordination
        private int _totalQueued = 0;
        private int _processedCount = 0;
        private int _historicalProcessedCount = 0;
        private int _streamingProcessedCount = 0;

        // Track processed instruments for straddle synthesis
        private readonly ConcurrentBag<(string ntSymbol, Instrument ntInstrument)> _processedInstruments = new ConcurrentBag<(string, Instrument)>();
        private readonly ConcurrentBag<string> _processedStraddleSymbols = new ConcurrentBag<string>();

        // BarsRequest management - keep alive for real-time tick storage
        private readonly ConcurrentDictionary<string, BarsRequest> _activeBarsRequests = new ConcurrentDictionary<string, BarsRequest>();

        // ═══════════════════════════════════════════════════════════════════
        // RATE LIMITING CONFIGURATION
        // ═══════════════════════════════════════════════════════════════════

        // Zerodha API rate limits: ~3 requests/second for historical data
        // With MaxDegreeOfParallelism=10, we batch process then throttle
        private const int HISTORICAL_MAX_PARALLELISM = 10;
        private const int HISTORICAL_BOUNDED_CAPACITY = 200;

        // Streaming (BarsRequest to NT) - can be faster since local operations
        private const int STREAMING_MAX_PARALLELISM = 6;
        private const int STREAMING_BOUNDED_CAPACITY = 100;

        // Subscription processing - sequential for thread safety
        private const int SUBSCRIPTION_BOUNDED_CAPACITY = 500;

        // Rate limiting semaphore for historical API calls
        private readonly SemaphoreSlim _historicalRateLimiter = new SemaphoreSlim(3, 3); // 3 concurrent API calls max
        private const int HISTORICAL_RATE_LIMIT_DELAY_MS = 350; // ~3 requests/second

        // ═══════════════════════════════════════════════════════════════════
        // REACTIVE HUB INTEGRATION
        // ═══════════════════════════════════════════════════════════════════

        // Reference to the reactive hub for publishing updates (single source of truth)
        private readonly MarketDataReactiveHub _hub = MarketDataReactiveHub.Instance;

        /// <summary>
        /// Inject a simulated price update (for SimulationService to feed Option Chain)
        /// </summary>
        public void InjectSimulatedPrice(string symbol, double price, DateTime? timestamp = null)
        {
            // Publish to reactive hub only (single source of truth)
            // Pass timestamp for simulation replay (shows historical tick time instead of current time)
            _hub.PublishOptionPrice(symbol, price, 0, "Simulated", timestamp);
        }

        // Flag to track if we've subscribed to the event-driven tick feed (0=false, 1=true)
        // Use int for Interlocked.CompareExchange atomic operations
        private int _subscribedToTickEvents = 0;

        private SubscriptionManager()
        {
            Logger.Info("[SubscriptionManager] Constructor: Initializing singleton instance with TPL Dataflow pipeline");
            InitializeDataflowPipeline();
            SubscribeToTickEvents();
        }

        /// <summary>
        /// Initializes the TPL Dataflow pipeline with proper backpressure and parallelism settings.
        /// This replaces the old ConcurrentQueue + Task.Run pattern with event-driven processing.
        /// </summary>
        private void InitializeDataflowPipeline()
        {
            Logger.Info("[SubscriptionManager] Initializing TPL Dataflow pipeline...");

            // Stage 1: Subscription Block - processes instruments sequentially
            // Sequential because NinjaTrader instrument creation requires UI thread coordination
            _subscriptionBlock = new ActionBlock<MappedInstrument>(
                async instrument =>
                {
                    try
                    {
                        int count = Interlocked.Increment(ref _processedCount);
                        Logger.Info($"[SubscriptionManager] Pipeline Stage 1: Processing {count}/{_totalQueued} - {instrument.symbol}");
                        await SubscribeToInstrument(instrument);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] Pipeline Stage 1 Error: {instrument.symbol} - {ex.Message}", ex);
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = 1, // Sequential for UI thread safety
                    BoundedCapacity = SUBSCRIPTION_BOUNDED_CAPACITY
                });

            // Stage 2: Historical Data Block - fetches data with rate limiting
            // Parallel with semaphore-based rate limiting to respect Zerodha API limits
            _historicalBlock = new ActionBlock<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)>(
                async item =>
                {
                    try
                    {
                        // Rate limiting: acquire semaphore before API call
                        await _historicalRateLimiter.WaitAsync();
                        try
                        {
                            int count = Interlocked.Increment(ref _historicalProcessedCount);
                            Logger.Debug($"[SubscriptionManager] Pipeline Stage 2: Historical fetch {count} - {item.ntSymbol}");
                            await TriggerBackfillAndUpdatePrice(item.mappedInst, item.ntSymbol, item.ntInstrument);
                        }
                        finally
                        {
                            // Release after a delay to enforce rate limit
                            _ = Task.Delay(HISTORICAL_RATE_LIMIT_DELAY_MS).ContinueWith(_ => _historicalRateLimiter.Release());
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] Pipeline Stage 2 Error: {item.ntSymbol} - {ex.Message}", ex);
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = HISTORICAL_MAX_PARALLELISM,
                    BoundedCapacity = HISTORICAL_BOUNDED_CAPACITY
                });

            // Stage 3: Streaming Block - sets up BarsRequest and VWAP
            // Moderate parallelism for NinjaTrader resource management
            _streamingBlock = new ActionBlock<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)>(
                async item =>
                {
                    try
                    {
                        int count = Interlocked.Increment(ref _streamingProcessedCount);
                        Logger.Debug($"[SubscriptionManager] Pipeline Stage 3: Streaming setup {count} - {item.ntSymbol}");
                        await RequestBarsForInstrumentStreaming(item.ntSymbol, item.ntInstrument, DateTime.Now.AddDays(-3), DateTime.Now);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] Pipeline Stage 3 Error: {item.ntSymbol} - {ex.Message}", ex);
                    }
                },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = STREAMING_MAX_PARALLELISM,
                    BoundedCapacity = STREAMING_BOUNDED_CAPACITY
                });

            // Monitor pipeline completion for straddle processing
            MonitorPipelineCompletion();

            Logger.Info("[SubscriptionManager] TPL Dataflow pipeline initialized successfully");
        }

        /// <summary>
        /// Monitors the streaming block completion to trigger straddle symbol processing.
        /// This replaces the polling loop in ProcessStreamingQueue.
        /// </summary>
        private void MonitorPipelineCompletion()
        {
            // When streaming block completes, process straddle symbols
            _streamingBlock.Completion.ContinueWith(async task =>
            {
                if (task.IsFaulted)
                {
                    Logger.Error($"[SubscriptionManager] Streaming block faulted: {task.Exception?.GetBaseException().Message}");
                    return;
                }

                Logger.Info("[SubscriptionManager] Streaming pipeline completed - processing straddle symbols");
                await ProcessStraddleSymbolsStreaming();

                // Signal batch completion
                _batchCompletionSource?.TrySetResult(true);

                // Reinitialize pipeline for next batch
                InitializeDataflowPipeline();
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// Subscribe to the event-driven tick feed from OptimizedTickProcessor.
        /// This is the reliable path for option price updates that bypasses callback chain issues.
        /// Uses Interlocked.CompareExchange to prevent double-subscription race condition.
        /// </summary>
        private void SubscribeToTickEvents()
        {
            // Atomic check-and-set: only proceed if we're the first to change 0 -> 1
            if (Interlocked.CompareExchange(ref _subscribedToTickEvents, 1, 0) != 0)
            {
                return; // Another thread already subscribed or is subscribing
            }

            try
            {
                var tickProcessor = MarketDataService.Instance.TickProcessor;
                if (tickProcessor != null)
                {
                    tickProcessor.OptionTickReceived += OnOptionTickReceived;
                    Logger.Info("[SubscriptionManager] Subscribed to OptimizedTickProcessor.OptionTickReceived event");
                }
                else
                {
                    // Reset flag so we can retry later
                    Interlocked.Exchange(ref _subscribedToTickEvents, 0);
                    Logger.Warn("[SubscriptionManager] TickProcessor not available yet - will retry on first subscription");
                }
            }
            catch (Exception ex)
            {
                // Reset flag on failure so we can retry
                Interlocked.Exchange(ref _subscribedToTickEvents, 0);
                Logger.Error($"[SubscriptionManager] Failed to subscribe to tick events: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler for option ticks from OptimizedTickProcessor.
        /// Publishes to reactive hub (single source of truth).
        /// </summary>
        private void OnOptionTickReceived(string symbol, double price)
        {
            if (string.IsNullOrEmpty(symbol) || price <= 0) return;

            // Publish to reactive hub only (single source of truth - enables batching and backpressure)
            _hub.PublishOptionPrice(symbol, price, 0, "WebSocket");
        }

        /// <summary>
        /// Queues a list of instruments for subscription processing using TPL Dataflow pipeline.
        /// This method posts to the subscription block and returns immediately (non-blocking).
        /// Use AwaitBatchCompletion() to wait for all processing to complete.
        /// </summary>
        public void QueueSubscription(List<MappedInstrument> instruments)
        {
            Logger.Info($"[SubscriptionManager] QueueSubscription(): Received {instruments.Count} instruments to queue via TPL Dataflow");

            // Ensure we're subscribed to tick events (retry if not done during construction)
            if (Interlocked.CompareExchange(ref _subscribedToTickEvents, 0, 0) == 0)
            {
                SubscribeToTickEvents();
            }

            // Reset counters for new batch
            _totalQueued = instruments.Count;
            Interlocked.Exchange(ref _processedCount, 0);
            Interlocked.Exchange(ref _historicalProcessedCount, 0);
            Interlocked.Exchange(ref _streamingProcessedCount, 0);

            // Create new completion source for this batch
            _batchCompletionSource = new TaskCompletionSource<bool>();

            // Post all instruments to the pipeline (non-blocking with backpressure)
            int postedCount = 0;
            foreach (var inst in instruments)
            {
                // SendAsync respects BoundedCapacity - will await if queue is full
                bool posted = _subscriptionBlock.Post(inst);
                if (posted)
                {
                    postedCount++;
                    Logger.Debug($"[SubscriptionManager] QueueSubscription(): Posted {inst.symbol} to pipeline");
                }
                else
                {
                    // Block is full or completed - use SendAsync for backpressure
                    Logger.Warn($"[SubscriptionManager] QueueSubscription(): Block full, using async post for {inst.symbol}");
                    _ = _subscriptionBlock.SendAsync(inst);
                    postedCount++;
                }
            }

            Logger.Info($"[SubscriptionManager] QueueSubscription(): Posted {postedCount}/{instruments.Count} instruments to TPL Dataflow pipeline");
        }

        /// <summary>
        /// Awaits completion of all subscription processing for the current batch.
        /// This provides an event-driven alternative to polling for completion.
        /// </summary>
        /// <param name="timeout">Optional timeout in milliseconds (default: 5 minutes)</param>
        /// <returns>True if batch completed successfully, false if timed out or faulted</returns>
        public async Task<bool> AwaitBatchCompletion(int timeout = 300000)
        {
            if (_batchCompletionSource == null) return true;

            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                {
                    var completedTask = await Task.WhenAny(_batchCompletionSource.Task, Task.Delay(timeout, cts.Token));
                    if (completedTask == _batchCompletionSource.Task)
                    {
                        cts.Cancel(); // Cancel the delay task
                        return await _batchCompletionSource.Task;
                    }
                    Logger.Warn($"[SubscriptionManager] AwaitBatchCompletion(): Timed out after {timeout}ms");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] AwaitBatchCompletion(): Error - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Signals that no more items will be posted to the subscription pipeline.
        /// Call this after QueueSubscription to allow the pipeline to complete gracefully.
        /// </summary>
        public void CompleteSubscriptionPipeline()
        {
            Logger.Info("[SubscriptionManager] CompleteSubscriptionPipeline(): Signaling subscription block completion");
            _subscriptionBlock.Complete();
        }

        // NOTE: ProcessQueue has been replaced by the TPL Dataflow _subscriptionBlock.
        // The ActionBlock processes items automatically with proper backpressure and parallelism control.

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
                    instrument.expiry.Value.ToString("yyyy-MM-dd"),
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

                        // Publish to reactive hub only (single source of truth)
                        _hub.PublishSymbolResolved(generatedSymbol, tradingSymbol);
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
            InstrumentManager.Instance.AddMappedInstrument(instrument.symbol, instrument);
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
                    var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;

                    if (adapter != null)
                    {
                        Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Adapter found, calling SubscribeMarketData");
                        string symbolForClosure = ntName;

                        adapter.SubscribeMarketData(ntInstrument, (type, price, size, time, unknown) =>
                        {
                            // Update via reactive hub (single source of truth)
                            // Note: Primary tick updates come from OptimizedTickProcessor via OnOptionTickReceived
                            // This callback is mainly for triggering NinjaTrader's internal data storage
                            if (type == MarketDataType.Last && price > 0)
                            {
                                // DIAGNOSTIC: Log first 50 option callbacks to verify they fire
                                bool isOption = symbolForClosure.Contains("CE") || symbolForClosure.Contains("PE");
                                if (isOption)
                                {
                                    Logger.Debug($"[SM-CALLBACK] {symbolForClosure} = {price} (type={type})");
                                }
                                // Publish to hub (will be deduplicated with OptimizedTickProcessor updates)
                                _hub.PublishOptionPrice(symbolForClosure, price, 0, "Callback");
                            }
                        });

                        Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({ntName}): Live subscription active");
                    }
                    else
                    {
                        Logger.Warn($"[SubscriptionManager] SubscribeToInstrument({ntName}): Adapter is NULL - cannot subscribe to live data");
                    }

                    // Step 6: Post to historical data pipeline (TPL Dataflow with rate limiting)
                    Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Posting to historical data pipeline");
                    bool posted = _historicalBlock.Post((instrument, ntName, ntInstrument));
                    if (!posted)
                    {
                        Logger.Warn($"[SubscriptionManager] SubscribeToInstrument({ntName}): Historical block full, using async post");
                        await _historicalBlock.SendAsync((instrument, ntName, ntInstrument));
                    }
                    Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({ntName}): Posted to historical pipeline");
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

        // NOTE: ProcessHistoricalDataQueue has been replaced by the TPL Dataflow _historicalBlock.
        // The ActionBlock provides:
        // - Automatic rate limiting via _historicalRateLimiter semaphore
        // - MaxDegreeOfParallelism=10 for parallel API calls within rate limits
        // - BoundedCapacity=200 for backpressure from subscription processing
        // - No polling loops or "Sleep & Hope" patterns

        /// <summary>
        /// Triggers historical data backfill for an instrument and updates the price in UI.
        /// After caching, queues the instrument for immediate BarsRequest + WebSocket subscription.
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

                    // Publish status to reactive hub only (single source of truth)
                    _hub.PublishOptionStatus(ntSymbol, $"Cached ({records.Count})");

                    // Track this instrument for straddle processing
                    _processedInstruments.Add((ntSymbol, instrument));

                    // Post to streaming pipeline (BarsRequest + VWAP setup)
                    // TPL Dataflow handles backpressure automatically
                    bool posted = _streamingBlock.Post((mappedInst, ntSymbol, instrument));
                    if (!posted)
                    {
                        Logger.Debug($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Streaming block full, using async post");
                        await _streamingBlock.SendAsync((mappedInst, ntSymbol, instrument));
                    }

                    // Cache bars for straddle computation if this is an option
                    if (!string.IsNullOrEmpty(mappedInst.option_type) && mappedInst.strike.HasValue)
                    {
                        string straddleSymbol = CacheOptionBarsForStraddle(mappedInst, apiSymbol, records);
                        if (!string.IsNullOrEmpty(straddleSymbol))
                        {
                            _processedStraddleSymbols.Add(straddleSymbol);
                        }
                    }

                    // Extract last price and update UI - BUT only if data is recent!
                    // Historical data API may return stale data (e.g., from 10:48 when querying at 13:27)
                    // If we blindly update price, we corrupt live WebSocket prices with old data
                    var lastRecord = records.OrderByDescending(r => r.TimeStamp).FirstOrDefault();
                    if (lastRecord != null && lastRecord.Close > 0)
                    {
                        var dataAge = DateTime.Now - lastRecord.TimeStamp;
                        Logger.Info($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): LastPrice={lastRecord.Close} from {lastRecord.TimeStamp:yyyy-MM-dd HH:mm} (age={dataAge.TotalMinutes:F1}min)");

                        // Only use historical price if it's within last 5 minutes
                        // Otherwise, live WebSocket price is more accurate
                        if (dataAge.TotalMinutes <= 5)
                        {
                            _hub.PublishOptionPrice(ntSymbol, lastRecord.Close, 0, "Historical");
                        }
                        else
                        {
                            Logger.Warn($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Skipping stale price update (age={dataAge.TotalMinutes:F1}min > 5min threshold)");
                        }
                    }
                }
                else
                {
                    Logger.Warn($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): No historical data received for {apiSymbol}");
                    _hub.PublishOptionStatus(ntSymbol, "No Data");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] TriggerBackfillAndUpdatePrice({ntSymbol}): Exception occurred - {ex.Message}", ex);
            }
        }

        // NOTE: ProcessStreamingQueue has been replaced by the TPL Dataflow _streamingBlock.
        // The ActionBlock provides:
        // - MaxDegreeOfParallelism=6 for balanced throughput
        // - BoundedCapacity=100 for backpressure from historical processing
        // - Completion event triggers straddle processing (see MonitorPipelineCompletion)
        // - No polling loops or manual batch collection

        /// <summary>
        /// Requests bars for a CE/PE instrument (streaming mode - immediate processing).
        /// </summary>
        private async Task RequestBarsForInstrumentStreaming(string ntSymbol, Instrument ntInstrument, DateTime fromDate, DateTime toDate)
        {
            try
            {
                // Check if we have cached data - use optimized small request
                bool hasCachedData = HistoricalBarCache.Instance.HasCachedData(ntSymbol, "minute", fromDate, toDate);
                int barsBack = hasCachedData ? 100 : 1500;

                Logger.Debug($"[SubscriptionManager] RequestBarsForInstrumentStreaming({ntSymbol}): Cache {(hasCachedData ? "HIT" : "MISS")} - using {barsBack} bars");

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var barsRequest = new BarsRequest(ntInstrument, barsBack);
                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Minute,
                            Value = 1
                        };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.Update += OnBarsRequestUpdate;

                        string symbolForClosure = ntSymbol;
                        Instrument instrumentForClosure = ntInstrument;
                        _activeBarsRequests.TryAdd(ntSymbol, barsRequest);

                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            int barCount = request.Bars?.Count ?? 0;
                            if (errorCode == ErrorCode.NoError)
                            {
                                Logger.Info($"[SubscriptionManager] Streaming BarsRequest completed for {symbolForClosure}: {barCount} bars");
                                _hub.PublishOptionStatus(symbolForClosure, $"Done ({barCount})");

                                // Start VWAP calculation immediately
                                _ = VWAPCalculatorService.Instance.StartVWAPCalculation(symbolForClosure, instrumentForClosure);
                            }
                            else
                            {
                                Logger.Warn($"[SubscriptionManager] Streaming BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                                _hub.PublishOptionStatus(symbolForClosure, $"Error: {errorCode}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[SubscriptionManager] RequestBarsForInstrumentStreaming({ntSymbol}): Error - {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] RequestBarsForInstrumentStreaming({ntSymbol}): Dispatcher error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Processes straddle symbols in streaming mode after CE/PE instruments are done.
        /// </summary>
        private async Task ProcessStraddleSymbolsStreaming()
        {
            var straddleSymbols = _processedStraddleSymbols.Distinct().ToList();
            if (straddleSymbols.Count == 0) return;

            Logger.Info($"[SubscriptionManager] ProcessStraddleSymbolsStreaming(): Processing {straddleSymbols.Count} STRDL symbols");

            DateTime fromDate = DateTime.Now.AddDays(-3);
            DateTime toDate = DateTime.Now;

            foreach (var straddleSymbol in straddleSymbols)
            {
                await RequestBarsForStraddleSymbol(straddleSymbol, fromDate, toDate);
            }

            Logger.Info($"[SubscriptionManager] ProcessStraddleSymbolsStreaming(): Completed all STRDL symbols");
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

        // NOTE: TriggerBarsRequestForAllInstruments has been replaced by the TPL Dataflow pipeline.
        // The streaming block now handles BarsRequest processing in real-time as historical data is cached,
        // rather than waiting for all historical data to complete before processing.

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
                                Logger.Debug($"[SubscriptionManager] BarsRequest completed for {symbolForClosure}: {barCount} bars inserted to NT DB");

                                // LOG SUBSCRIPTION STATE
                                var refDetails = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Debug($"[SubscriptionManager] PRE-DONE STATUS: {symbolForClosure} - RefCount={refDetails.Count}, Sticky={refDetails.IsSticky}, Consumers={string.Join(",", refDetails.Consumers)}");

                                // Update status via hub (single source of truth)
                                _hub.PublishOptionStatus(symbolForClosure, $"Done ({barCount})");

                                // LOG SUBSCRIPTION STATE AGAIN
                                var refDetailsAfter = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Debug($"[SubscriptionManager] POST-DONE STATUS: {symbolForClosure} - RefCount={refDetailsAfter.Count}, Sticky={refDetailsAfter.IsSticky}, Consumers={string.Join(",", refDetailsAfter.Consumers)}");

                                // Start VWAP calculation for this instrument using hidden BarsRequest
                                _ = VWAPCalculatorService.Instance.StartVWAPCalculation(symbolForClosure, instrumentForClosure);
                            }
                            else
                            {
                                Logger.Warn($"[SubscriptionManager] BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                                _hub.PublishOptionStatus(symbolForClosure, $"Error: {errorCode}");
                            }

                            // IMPORTANT: Do NOT dispose BarsRequest - keep it alive for real-time tick storage
                            // When BarsRequest is active with Update handler, NinjaTrader writes incoming ticks to its database.
                            // Disposing it stops real-time tick storage (only historical bars would be saved).
                            // We keep the BarsRequest in _activeBarsRequests to maintain the live subscription.
                            Logger.Debug($"[SubscriptionManager] BarsRequest for {symbolForClosure} completed - keeping alive for real-time tick storage");
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
                                Logger.Debug($"[SubscriptionManager] STRDL BarsRequest completed for {symbolForClosure}: {barCount} bars inserted to NT DB");

                                // LOG SUBSCRIPTION STATE
                                var refDetails = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Debug($"[SubscriptionManager] PRE-DONE STATUS (STRDL): {symbolForClosure} - RefCount={refDetails.Count}, Sticky={refDetails.IsSticky}, Consumers={string.Join(",", refDetails.Consumers)}");

                                // Update status via hub (single source of truth)
                                _hub.PublishOptionStatus(symbolForClosure, $"Done ({barCount})");

                                // LOG SUBSCRIPTION STATE AGAIN
                                var refDetailsAfter = SubscriptionTrackingService.Instance.GetReferenceDetails(symbolForClosure);
                                Logger.Debug($"[SubscriptionManager] POST-DONE STATUS (STRDL): {symbolForClosure} - RefCount={refDetailsAfter.Count}, Sticky={refDetailsAfter.IsSticky}, Consumers={string.Join(",", refDetailsAfter.Consumers)}");

                                // Start VWAP calculation for this STRDL instrument using hidden BarsRequest
                                _ = VWAPCalculatorService.Instance.StartVWAPCalculation(symbolForClosure, instrumentForClosure);
                            }
                            else
                            {
                                Logger.Warn($"[SubscriptionManager] STRDL BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                                _hub.PublishOptionStatus(symbolForClosure, $"Error: {errorCode}");
                            }

                            // IMPORTANT: Do NOT dispose BarsRequest - keep it alive for real-time tick storage
                            // When BarsRequest is active with Update handler, NinjaTrader writes incoming ticks to its database.
                            // Disposing it stops real-time tick storage (only historical bars would be saved).
                            // We keep the BarsRequest in _activeBarsRequests to maintain the live subscription.
                            Logger.Debug($"[SubscriptionManager] STRDL BarsRequest for {symbolForClosure} completed - keeping alive for real-time tick storage");
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
