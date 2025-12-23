using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using QABrokerAPI.Common.Enums;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Instruments;
using QANinjaAdapter.Services.MarketData;

namespace QANinjaAdapter.Services.Analysis
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
            _logic.StatusUpdated += msg => Logger.Info($"[MarketAnalyzerService] StatusUpdate: {msg}");

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
                        var adapter = Connector.Instance.GetAdapter() as QAAdapter;
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

            var adapter = Connector.Instance.GetAdapter() as QAAdapter;
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
                        Logger.Debug($"[MarketAnalyzerService] MarketData({instrumentName}): LastClose={price}");
                        _logic.UpdatePrice(instrumentName, price, price);
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
