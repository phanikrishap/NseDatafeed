using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private bool _isProcessing = false;
        private int _processedCount = 0;
        private int _totalQueued = 0;

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

                    // Small delay to prevent flooding the system
                    await Task.Delay(50);
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

            // Step 1: Register in Instrument Manager (Memory + JSON)
            Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Step 1 - Adding to InstrumentManager");
            InstrumentManager.Instance.AddMappedInstrument(instrument);
            Logger.Info($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Registered in InstrumentManager");

            // Step 2: Create NT MasterInstrument
            Logger.Debug($"[SubscriptionManager] SubscribeToInstrument({instrument.symbol}): Step 2 - Creating InstrumentDefinition");
            var instrumentDef = new InstrumentDefinition
            {
                Symbol = instrument.symbol,
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

                        adapter.SubscribeMarketData(ntInstrument, (type, price, size, time, unknown) =>
                        {
                            // Autonomous data flow - just log periodically
                            if (type == MarketDataType.Last)
                            {
                                Logger.Debug($"[SubscriptionManager] LiveData({ntName}): Last={price}");
                            }
                        });

                        Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Live subscription active");
                    }
                    else
                    {
                        Logger.Warn($"[SubscriptionManager] SubscribeToInstrument({ntName}): Adapter is NULL - cannot subscribe to live data");
                    }

                    // Step 6: Trigger Backfill (3 Days, 1-Min and Tick)
                    Logger.Info($"[SubscriptionManager] SubscribeToInstrument({ntName}): Step 6 - Triggering background backfill");
                    _ = Task.Run(async () =>
                    {
                        await TriggerBackfill(ntName, ntInstrument);
                    });
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
        /// Triggers historical data backfill for an instrument
        /// </summary>
        private async Task TriggerBackfill(string symbol, Instrument instrument)
        {
            Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Starting 3-day backfill");

            try
            {
                DateTime end = DateTime.Now;
                DateTime start = end.AddDays(-3);

                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Date range {start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}");

                // Request 1-Minute Data
                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Requesting 1-Minute historical data...");

                await Connector.Instance.GetHistoricalTrades(
                    BarsPeriodType.Minute,
                    symbol,
                    start,
                    end,
                    MarketType.UsdM,
                    null // No UI progress bar for background task
                );

                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): 1-Minute data request completed");

                // Stagger between requests
                await Task.Delay(1000);

                // Request Tick Data (Note: Zerodha may not support tick data)
                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Requesting Tick historical data...");

                await Connector.Instance.GetHistoricalTrades(
                    BarsPeriodType.Tick,
                    symbol,
                    start,
                    end,
                    MarketType.UsdM,
                    null
                );

                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Tick data request completed");
                Logger.Info($"[SubscriptionManager] TriggerBackfill({symbol}): Backfill completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[SubscriptionManager] TriggerBackfill({symbol}): Exception occurred - {ex.Message}", ex);
            }
        }
    }
}
