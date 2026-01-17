using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Manages the subscription work queue for instrument registration.
    /// Handles sequential processing of instruments through the subscription workflow:
    /// Registration -> NT Creation -> Live Subscription -> Queue for Backfill
    /// Extracted from SubscriptionManager for better separation of concerns.
    /// </summary>
    public class SubscriptionWorkQueue
    {
        #region Singleton

        private static SubscriptionWorkQueue _instance;
        public static SubscriptionWorkQueue Instance => _instance ?? (_instance = new SubscriptionWorkQueue());

        #endregion

        #region State

        private readonly ConcurrentQueue<MappedInstrument> _subscriptionQueue = new ConcurrentQueue<MappedInstrument>();
        private int _isProcessing = 0; // 0 = false, 1 = true (for Interlocked)
        private int _processedCount = 0;
        private int _totalQueued = 0;

        #endregion

        #region Events

        /// <summary>
        /// Fired when zerodhaSymbol is resolved from DB (generatedSymbol, zerodhaSymbol)
        /// </summary>
        public event Action<string, string> SymbolResolved;

        /// <summary>
        /// Fired when an instrument is successfully subscribed and ready for backfill
        /// </summary>
        public event Action<MappedInstrument, string, Instrument> InstrumentSubscribed;

        /// <summary>
        /// Fired when processing progress updates (current, total)
        /// </summary>
        public event Action<int, int> ProgressUpdated;

        /// <summary>
        /// Fired when all queue processing is complete
        /// </summary>
        public event Action QueueProcessingComplete;

        #endregion

        #region Properties

        /// <summary>
        /// Whether queue processing is currently running
        /// </summary>
        public bool IsProcessing => _isProcessing == 1;

        /// <summary>
        /// Count of items in the queue
        /// </summary>
        public int QueueCount => _subscriptionQueue.Count;

        /// <summary>
        /// Number of items processed so far
        /// </summary>
        public int ProcessedCount => _processedCount;

        /// <summary>
        /// Total items queued for processing
        /// </summary>
        public int TotalQueued => _totalQueued;

        #endregion

        #region Constructor

        private SubscriptionWorkQueue()
        {
            Logger.Info("[SubscriptionWorkQueue] Constructor: Initializing singleton instance");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Queues a list of instruments for subscription processing
        /// </summary>
        public void QueueSubscription(List<MappedInstrument> instruments)
        {
            Logger.Info($"[SubscriptionWorkQueue] QueueSubscription(): Received {instruments.Count} instruments to queue");

            _totalQueued = instruments.Count;
            _processedCount = 0;

            foreach (var inst in instruments)
            {
                _subscriptionQueue.Enqueue(inst);
                Logger.Debug($"[SubscriptionWorkQueue] Enqueued {inst.symbol}");
            }

            Logger.Info($"[SubscriptionWorkQueue] Queue size is now {_subscriptionQueue.Count}");

            TryStartProcessing();
        }

        /// <summary>
        /// Queue a single instrument
        /// </summary>
        public void QueueSingleInstrument(MappedInstrument instrument)
        {
            _subscriptionQueue.Enqueue(instrument);
            Interlocked.Increment(ref _totalQueued);

            TryStartProcessing();
        }

        /// <summary>
        /// Atomically try to start processing if not already running
        /// </summary>
        private void TryStartProcessing()
        {
            // Atomically try to set _isProcessing from 0 to 1
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0)
            {
                Logger.Info("[SubscriptionWorkQueue] Starting queue processor...");
                Task.Run(ProcessQueue);
            }
            else
            {
                Logger.Info("[SubscriptionWorkQueue] Queue processor already running");
            }
        }

        #endregion

        #region Queue Processing

        /// <summary>
        /// Processes the subscription queue one instrument at a time
        /// </summary>
        private async Task ProcessQueue()
        {
            Logger.Info("[SubscriptionWorkQueue] ProcessQueue(): Started processing");
            // Note: _isProcessing is already set to 1 by TryStartProcessing via CompareExchange

            bool shouldFireCompletion = false;

            try
            {
                while (true)
                {
                    // Process all available items
                    while (_subscriptionQueue.TryDequeue(out var instrument))
                    {
                        int processed = Interlocked.Increment(ref _processedCount);
                        Logger.Info($"[SubscriptionWorkQueue] Processing {processed}/{_totalQueued} - {instrument.symbol}");

                        try
                        {
                            await SubscribeToInstrument(instrument);
                            Logger.Debug($"[SubscriptionWorkQueue] Completed {instrument.symbol}");

                            // Notify progress
                            ProgressUpdated?.Invoke(processed, _totalQueued);

                            // Minimal delay - subscriptions are lightweight
                            await Task.Delay(10);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[SubscriptionWorkQueue] Failed to process {instrument.symbol} - {ex.Message}", ex);
                        }
                    }

                    // Try to exit: reset _isProcessing to 0, but re-check queue
                    // If an item was added between TryDequeue returning false and Exchange,
                    // we need to continue processing
                    Interlocked.Exchange(ref _isProcessing, 0);

                    // Double-check: if queue is still empty, we're done
                    if (_subscriptionQueue.IsEmpty)
                    {
                        // We truly finished - queue is empty and we released the lock
                        shouldFireCompletion = true;
                        break;
                    }

                    // Items were added after we exited the loop - try to reclaim processing
                    if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
                    {
                        // Another processor started, let it handle the work and completion
                        shouldFireCompletion = false;
                        break;
                    }
                    // We reclaimed processing, continue the outer loop
                }

                if (shouldFireCompletion)
                {
                    Logger.Info($"[SubscriptionWorkQueue] Completed all {_processedCount} instruments");
                    QueueProcessingComplete?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionWorkQueue] ProcessQueue exception: {ex.Message}", ex);
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }

        /// <summary>
        /// Subscribes to a single instrument - full workflow
        /// </summary>
        private async Task SubscribeToInstrument(MappedInstrument instrument)
        {
            Logger.Debug($"[SubscriptionWorkQueue] SubscribeToInstrument({instrument.symbol}): Starting subscription workflow");

            // Step 0: Look up instrument token from SQLite database by segment/underlying/expiry/strike/option_type
            if (instrument.instrument_token == 0 && instrument.expiry.HasValue && instrument.strike.HasValue && !string.IsNullOrEmpty(instrument.option_type))
            {
                Logger.Debug($"[SubscriptionWorkQueue] Looking up option token in SQLite...");

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
                        Logger.Info($"[SubscriptionWorkQueue] Found token={token}, zerodhaSymbol={tradingSymbol}");

                        // Notify UI of symbol resolution
                        SymbolResolved?.Invoke(generatedSymbol, tradingSymbol);
                    }
                }
                else
                {
                    Logger.Warn($"[SubscriptionWorkQueue] Option token not found in SQLite for {instrument.symbol} - cannot subscribe");
                    return;
                }
            }

            // Step 1: Register in Instrument Manager (Memory + JSON)
            InstrumentManager.Instance.AddMappedInstrument(instrument.symbol, instrument);
            Logger.Debug($"[SubscriptionWorkQueue] Registered {instrument.symbol} in InstrumentManager");

            // Step 2: Create NT MasterInstrument
            var instrumentDef = new InstrumentDefinition
            {
                Symbol = instrument.zerodhaSymbol ?? instrument.symbol,
                BrokerSymbol = instrument.zerodhaSymbol ?? instrument.symbol,
                Segment = instrument.segment?.Contains("BFO") == true ? "BSE" : "NSE",
                MarketType = MarketType.UsdM
            };

            string ntName = "";
            bool created = false;

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    created = InstrumentManager.Instance.CreateInstrument(instrumentDef, out ntName);
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionWorkQueue] CreateInstrument exception for {instrument.symbol} - {ex.Message}", ex);
                return;
            }

            if (!created && string.IsNullOrEmpty(ntName))
            {
                Logger.Error($"[SubscriptionWorkQueue] CreateInstrument failed for {instrument.symbol}");
                return;
            }

            Logger.Debug($"[SubscriptionWorkQueue] NT Instrument created/found as '{ntName}'");

            // Step 3: Get NT Instrument handle
            Instrument ntInstrument = null;

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    ntInstrument = Instrument.GetInstrument(ntName);
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionWorkQueue] GetInstrument exception for {ntName} - {ex.Message}", ex);
                return;
            }

            if (ntInstrument == null)
            {
                Logger.Error($"[SubscriptionWorkQueue] GetInstrument returned NULL for {ntName}");
                return;
            }

            Logger.Debug($"[SubscriptionWorkQueue] Got NT Instrument handle for {ntName}");

            // Step 4: Live Subscription - Let SubscriptionManager handle this via event
            // (SubscriptionManager has access to the adapter and callback registration)

            // Notify that instrument is ready for live subscription and backfill
            InstrumentSubscribed?.Invoke(instrument, ntName, ntInstrument);
        }

        #endregion
    }
}
