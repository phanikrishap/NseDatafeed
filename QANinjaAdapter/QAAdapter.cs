using QAAdapterAddOn.ViewModels;
using QABrokerAPI.Common.Enums;
using QABrokerAPI.Zerodha.Websockets;
using QANinjaAdapter.Classes;
using QANinjaAdapter.Controls;
using QANinjaAdapter.Helpers;
using QANinjaAdapter.Models;
using QANinjaAdapter.Models.MarketData;
using QANinjaAdapter.Services.MarketData;
using QANinjaAdapter.ViewModels;
using NinjaTrader.Adapter;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Adapters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NinjaTrader.CQG.ProtoBuf;
using System.Data.SQLite;
using QANinjaAdapter.SyntheticInstruments;

#nullable disable
namespace QANinjaAdapter
{
    public class QAAdapter : AdapterBase, IAdapter, IDisposable
    {
        private IConnection _ninjaConnection;
        private QAConnectorOptions _options;
        private readonly ConcurrentDictionary<string, L1Subscription> _l1Subscriptions = new ConcurrentDictionary<string, L1Subscription>();
        private readonly ConcurrentDictionary<string, L2Subscription> _l2Subscriptions = new ConcurrentDictionary<string, L2Subscription>();
        private readonly HashSet<string> _marketLiveDataSymbols = new HashSet<string>();
        private readonly HashSet<string> _marketDepthDataSymbols = new HashSet<string>();
        private static readonly object _lockLiveSymbol = new object();
        private static readonly object _lockDepthSymbol = new object();
        private readonly object _marketDataLock = new object(); // Added for synthetic straddle data synchronization

        // Instance tracking
        private static int _instanceCounter = 0;
        private readonly int _instanceId;

        public QAAdapter()
        {
            _instanceId = Interlocked.Increment(ref _instanceCounter);
            Logger.Info($"[QAAdapter] INSTANCE CREATED: ID={_instanceId}");
        }

        private SyntheticInstruments.SyntheticStraddleService _syntheticStraddleService;

        /// <summary>
        /// Gets the SyntheticStraddleService for straddle price events
        /// </summary>
        public SyntheticInstruments.SyntheticStraddleService SyntheticStraddleService => _syntheticStraddleService;

        // Removed LogMe: Use Logger.Info or NinjaTrader native logging directly.

        public void Connect(IConnection connection)
        {
            Logger.Info("QAAdapter: Initializing and connecting adapter...");
            this._ninjaConnection = connection;
            this._options = (QAConnectorOptions)this._ninjaConnection.Options;
            
            // Set the adapter instance in the Connector class
            Connector.SetAdapter(this);
            
            // Initialize the SyntheticStraddleService
            _syntheticStraddleService = new SyntheticInstruments.SyntheticStraddleService(this);
            
            this._ninjaConnection.OrderTypes = new NinjaTrader.Cbi.OrderType[4]
            {
                NinjaTrader.Cbi.OrderType.Market,
                NinjaTrader.Cbi.OrderType.Limit,
                NinjaTrader.Cbi.OrderType.StopMarket,
                NinjaTrader.Cbi.OrderType.StopLimit
            };
            this._ninjaConnection.TimeInForces = new NinjaTrader.Cbi.TimeInForce[3]
            {
                NinjaTrader.Cbi.TimeInForce.Day,
                NinjaTrader.Cbi.TimeInForce.Gtc,
                NinjaTrader.Cbi.TimeInForce.Gtd
            };
            this._ninjaConnection.Features = new NinjaTrader.Cbi.Feature[10]
            {
                NinjaTrader.Cbi.Feature.Bars1Minute,
                NinjaTrader.Cbi.Feature.BarsDaily,
                NinjaTrader.Cbi.Feature.BarsTick,
                NinjaTrader.Cbi.Feature.BarsTickIntraday,
                NinjaTrader.Cbi.Feature.MarketData,
                NinjaTrader.Cbi.Feature.AtmStrategies,
                NinjaTrader.Cbi.Feature.Order,
                NinjaTrader.Cbi.Feature.OrderChange,
                NinjaTrader.Cbi.Feature.CustomOrders,
                NinjaTrader.Cbi.Feature.MarketDepth
            };
            this._ninjaConnection.InstrumentTypes = new InstrumentType[3]
            {
                InstrumentType.Stock,
                InstrumentType.Future,
                InstrumentType.Option
            };
            this._ninjaConnection.MarketDataTypes = new MarketDataType[1]
            {
                MarketDataType.Last
            };
            this.Connect();
        }

        private async void Connect()
        {
            if (this._ninjaConnection.Status == ConnectionStatus.Connecting)
            {
                if (Connector.Instance.CheckConnection())
                {
                    Logger.Info("QAAdapter: Connection to provider (Zerodha) successful.");
                    this.SetInstruments();
                    this._ninjaConnection.ConnectionStatusCallback(ConnectionStatus.Connected, ConnectionStatus.Connected, ErrorCode.NoError, "");

                    await Connector.Instance.RegisterInstruments();
                }
                else
                    this._ninjaConnection.ConnectionStatusCallback(ConnectionStatus.Disconnected, ConnectionStatus.Disconnected, ErrorCode.LogOnFailed, "Unable to connect to provider Zerodha.");
            }
            else
                this.Disconnect();
        }

        private void SetInstruments()
        {
            DataContext.Instance.SymbolNames.Clear();
            // Changed from CryptoCurrency to Stock for Zerodha
            foreach (MasterInstrument masterInstrument in MasterInstrument.All.Where<MasterInstrument>((Func<MasterInstrument, bool>)(x =>
                (x.InstrumentType == InstrumentType.Stock ||
                 x.InstrumentType == InstrumentType.Future ||
                 x.InstrumentType == InstrumentType.Option) &&
                !string.IsNullOrEmpty(((IEnumerable<string>)x.ProviderNames).ElementAtOrDefault<string>(Constants.ProviderId)))))
            {
                if (!DataContext.Instance.SymbolNames.ContainsKey(masterInstrument.Name))
                    DataContext.Instance.SymbolNames.Add(masterInstrument.Name, masterInstrument.ProviderNames[Constants.ProviderId]);
            }
        }

        protected override void OnStateChange()
        {
            if (this.State == State.SetDefaults)
            {

                this.Name = "QA Adapter";
                //this.DisplayName = "QANinjaAdapter";
                //this.DisplayName = "QANinjaAdapter";
            }
            if (this.State != State.Configure)
                return;
        }

        public void Disconnect()
        {
            lock (QAAdapter._lockLiveSymbol)
                this._marketLiveDataSymbols?.Clear();
            lock (QAAdapter._lockDepthSymbol)
                this._marketDepthDataSymbols?.Clear();
            if (this._ninjaConnection.Status == ConnectionStatus.Disconnected)
                return;
            this._ninjaConnection.ConnectionStatusCallback(ConnectionStatus.Disconnected, ConnectionStatus.Disconnected, ErrorCode.NoError, string.Empty);
        }

        public void ResolveInstrument(Instrument instrument, Action<Instrument, ErrorCode, string> callback) { }

        public void SubscribeFundamentalData(Instrument instrument, Action<FundamentalDataType, object> callback) { }

        public void UnsubscribeFundamentalData(Instrument instrument) { }

        public void SubscribeMarketData(
    Instrument instrument,
    Action<MarketDataType, double, long, DateTime, long> callback)
        {
            try
            {
                if (this._ninjaConnection.Trace.MarketData)
                    this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                        $"({this._options.Name}) QAAdapter.SubscribeMarketData: instrument='{instrument.FullName}'"));

                if (this._ninjaConnection.Status == ConnectionStatus.Disconnecting ||
                    this._ninjaConnection.Status == ConnectionStatus.Disconnected)
                    return;

                string name = instrument.MasterInstrument.Name;
                if (string.IsNullOrEmpty(name))
                    return;

                // Handle Synthetic Straddle Subscription
                if (_syntheticStraddleService != null && _syntheticStraddleService.IsSyntheticSymbol(name))
                {
                    // 1. Register subscription for SYNTHETIC symbol (parent)
                    if (!_l1Subscriptions.ContainsKey(name))
                    {
                        var sub = new L1Subscription
                        {
                            Instrument = instrument,
                            IsIndex = false
                        };
                        sub.TryAddCallback(instrument, callback);
                        _l1Subscriptions.TryAdd(name, sub);
                    }
                    else
                    {
                        _l1Subscriptions[name].TryAddCallback(instrument, callback);
                    }

                    // 2. Subscribe to legs
                    var def = _syntheticStraddleService.GetDefinition(name);
                    if (def != null)
                    {
                         Logger.Info($"QAAdapter: Subscribing to synthetic straddle {name} (Legs: {def.CESymbol}, {def.PESymbol})");
                         Logger.Debug($"QAAdapter: About to call SubscribeToSyntheticLeg for CE: {def.CESymbol}");
                         SubscribeToSyntheticLeg(def.CESymbol);
                         Logger.Debug($"QAAdapter: About to call SubscribeToSyntheticLeg for PE: {def.PESymbol}");
                         SubscribeToSyntheticLeg(def.PESymbol);
                         Logger.Debug($"QAAdapter: Completed leg subscriptions for {name}");
                    }
                    else
                    {
                        Logger.Error($"QAAdapter: Straddle definition is NULL for {name}!");
                    }
                    return; // Don't send synthetic symbol to gateway
                }

                string nativeSymbolName = name;
                MarketType mt = MarketType.Spot;

                // First handle the subscription object to avoid race conditions
                L1Subscription l1Subscription;

                if (!this._l1Subscriptions.TryGetValue(nativeSymbolName, out l1Subscription))
                {
                    // Check if this is an index symbol (no volume traded, price updates only)
                    bool isIndex = IsIndexSymbol(nativeSymbolName);

                    // Create a new subscription with thread-safe callback management
                    l1Subscription = new L1Subscription
                    {
                        Instrument = instrument,
                        IsIndex = isIndex
                    };

                    // Add the callback using thread-safe method
                    l1Subscription.TryAddCallback(instrument, callback);

                    // Add to dictionary
                    this._l1Subscriptions.TryAdd(nativeSymbolName, l1Subscription);

                    Logger.Info($"[QAAdapter] SubscribeMarketData: NEW subscription for '{nativeSymbolName}', callbacks={l1Subscription.CallbackCount}, instrument={instrument.FullName}");

                    if (isIndex)
                        Logger.Info($"QAAdapter.SubscribeMarketData: Subscribed to INDEX symbol '{nativeSymbolName}' (price updates without volume)");
                }
                else
                {
                    // CRITICAL: AddCallback always adds a new callback (supports multiple subscribers)
                    // NinjaTrader reuses the same Instrument object, so we use unique callback IDs internally
                    int beforeCount = l1Subscription.CallbackCount;
                    l1Subscription.AddCallback(instrument, callback);
                    Logger.Info($"[QAAdapter] SubscribeMarketData: ADDED callback for '{nativeSymbolName}', callbacks={beforeCount}->{l1Subscription.CallbackCount}, instrument={instrument.FullName}");
                }

                // Update OptimizedTickProcessor cache immediately so new subscriptions receive ticks
                MarketDataService.Instance.UpdateProcessorSubscriptionCache(this._l1Subscriptions);

                // Now handle the websocket subscription
                lock (QAAdapter._lockLiveSymbol)
                {
                    // CRITICAL FIX: Always call SubscribeToTicks regardless of local cache.
                    // The SubscriptionTrackingService handles reference counting and deduplication.
                    // If we block it here, we preven second consumers (Straddles) from registering their reference,
                    // causing the subscription to die when the first consumer (Option Chain) exits.

                    string originalSymbol = Connector.GetSymbolName(name, out mt);

                    // CRITICAL FIX: Detect MCX symbols by checking for CRUDE, GOLD, SILVER, etc.
                    // The GetSymbolName method doesn't properly detect MCX from option symbols like CRUDEOIL26JAN5200CE
                    if (name.StartsWith("CRUDEOIL", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("SILVER", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("NATURALGAS", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("COPPER", StringComparison.OrdinalIgnoreCase))
                    {
                        mt = MarketType.MCX;
                        Logger.Info($"[SUBSCRIBE] Detected MCX commodity symbol: {name}, forcing MarketType=MCX");
                    }

                    // Always add to local tracking if not present (for UI status etc)
                    if (!this._marketLiveDataSymbols.Contains(nativeSymbolName))
                    {
                        this._marketLiveDataSymbols.Add(nativeSymbolName);
                    }

                    Logger.Info($"[SUBSCRIBE] Initiating WebSocket for {name} (originalSymbol={originalSymbol}, marketType={mt})");

                    // CRITICAL: Capture variables for closure to avoid race conditions
                    string capturedName = name;
                    string capturedNativeSymbol = nativeSymbolName;
                    string capturedOriginalSymbol = originalSymbol;
                    MarketType capturedMarketType = mt;

                    // Use dedicated Thread to completely bypass thread pool starvation
                    var thread = new Thread(() => {
                        try
                        {
                            Logger.Info($"[SUBSCRIBE] Thread STARTED for {capturedName}");
                            // Run the async method synchronously on this dedicated thread
                            Connector.Instance.SubscribeToTicks(
                                capturedNativeSymbol,
                                capturedMarketType,
                                capturedOriginalSymbol,
                                this._l1Subscriptions,
                                new WebSocketConnectionFunc((Func<bool>)(() =>
                                {
                                    lock (QAAdapter._lockLiveSymbol)
                                        return !this._marketLiveDataSymbols.Contains(capturedNativeSymbol);
                                }))
                            ).GetAwaiter().GetResult();
                            Logger.Info($"[SUBSCRIBE] SubscribeToTicks completed for {capturedName}");
                        }
                        catch (Exception ex)
                        {
                            // Log error but also remove the symbol from active symbols
                            // to allow retry on next subscription request
                            lock (QAAdapter._lockLiveSymbol)
                            {
                                if (this._marketLiveDataSymbols.Contains(capturedNativeSymbol))
                                    this._marketLiveDataSymbols.Remove(capturedNativeSymbol);
                            }
                            Logger.Error($"[SUBSCRIBE] Error subscribing to {capturedName}: {ex.Message}");

                            if (this._ninjaConnection.Trace.Connect)
                                this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                                    $"({this._options.Name}) QAAdapter.SubscribeToTicks Exception={ex}"));
                        }
                    });
                    thread.IsBackground = true;
                    thread.Name = $"WS_{capturedName}";
                    thread.Start();
                    Logger.Info($"[SUBSCRIBE] Thread launched for {capturedName}, ThreadId={thread.ManagedThreadId}");
                }
            }
            catch (Exception ex)
            {
                if (this._ninjaConnection.Trace.Connect)
                    this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                        $"({this._options.Name}) QAAdapter.SubscribeMarketData Exception={ex}"));
            }
        }

        public void UnsubscribeMarketData(Instrument instrument)
        {
            string name = instrument.MasterInstrument.Name;

            // CRITICAL FIX: NinjaTrader aggressively calls UnsubscribeMarketData during BarsRequest
            // historical data operations. If we actually remove callbacks, charts and Option Chain
            // stop receiving live ticks.
            //
            // SOLUTION: Make subscriptions "sticky" - once subscribed, keep receiving data.
            // We only log the unsubscribe attempt but DO NOT remove the callback.
            // This ensures continuous tick flow even during historical backfills.
            //
            // The WebSocket subscription is managed by SubscriptionTrackingService with reference
            // counting, so we don't need to worry about resource cleanup here.

            if (_l1Subscriptions.TryGetValue(name, out var subscription))
            {
                // Thread-safe check using new L1Subscription methods
                if (subscription.ContainsCallback(instrument))
                {
                    // STICKY SUBSCRIPTION: Log but DO NOT remove the callback
                    Logger.Info($"[UNSUBSCRIBE] [Instance={_instanceId}] IGNORED unsubscribe request for {name} (keeping callback alive), total callbacks: {subscription.CallbackCount}");

                    // NOTE: We intentionally DO NOT call:
                    // subscription.TryRemoveCallback(instrument);
                    // MarketDataService.Instance.UpdateProcessorSubscriptionCache(this._l1Subscriptions);
                }
                else
                {
                    Logger.Info($"[UNSUBSCRIBE] [Instance={_instanceId}] No callback found for {name} (instrument={instrument.FullName}), total callbacks: {subscription.CallbackCount}");
                }
            }

            // NOTE: We intentionally do NOT remove from _marketLiveDataSymbols or callbacks.
            // The WebSocket subscription is managed by SubscriptionTrackingService.
            // Subscriptions are "sticky" - they stay active until the adapter disconnects.
        }

        public void SubscribeMarketDepth(
            Instrument instrument,
            Action<int, string, Operation, MarketDataType, double, long, DateTime> callback)
        {
            //NinjaTrader.NinjaScript.NinjaScript.Log($"DEBUG-CALL: SubscribeMarketData called for {instrument.FullName}", NinjaTrader.Cbi.LogLevel.Error); // Use Error level to make it stand out
            try
            {
                if (this._ninjaConnection.Trace.MarketDepth)
                    this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketDepth: instrument='{instrument.FullName}'"));
                if (this._ninjaConnection.Status == ConnectionStatus.Disconnecting || this._ninjaConnection.Status == ConnectionStatus.Disconnected)
                    return;
                string name = instrument.MasterInstrument.Name;
                MarketType mt = MarketType.Spot;
                if (string.IsNullOrEmpty(name))
                    return;
                string nativeSymbolName = name;
                lock (QAAdapter._lockDepthSymbol)
                {
                    if (!this._marketDepthDataSymbols.Contains(name))
                    {
                        string originalSymbol = Connector.GetSymbolName(name, out mt);
                        this._marketDepthDataSymbols.Add(nativeSymbolName);
                        Task.Run((Func<Task>)(() => Connector.Instance.SubscribeToDepth(nativeSymbolName, mt, originalSymbol, this._l2Subscriptions, new WebSocketConnectionFunc((Func<bool>)(() =>
                        {
                            lock (QAAdapter._lockDepthSymbol)
                                return !this._marketDepthDataSymbols.Contains(nativeSymbolName);
                        })))));
                    }
                }
                L2Subscription l2Subscription1;
                this._l2Subscriptions.TryGetValue(name, out l2Subscription1);
                if (l2Subscription1 == null)
                {
                    ConcurrentDictionary<string, L2Subscription> l2Subscriptions = this._l2Subscriptions;
                    string key = name;
                    L2Subscription l2Subscription2 = new L2Subscription();
                    l2Subscription2.Instrument = instrument;
                    l2Subscription1 = l2Subscription2;
                    l2Subscriptions.TryAdd(key, l2Subscription2);
                }
                l2Subscription1.L2Callbacks = new SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>((IDictionary<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>)l2Subscription1.L2Callbacks)
                {
                    {
                        instrument,
                        callback
                    }
                };
                int status = (int)this._ninjaConnection.Status;
            }
            catch (Exception ex)
            {
                if (!this._ninjaConnection.Trace.Connect)
                    return;
                this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketDepth Exception={ex.ToString()}"));
            }
        }

        public void UnsubscribeMarketDepth(Instrument instrument)
        {
            string name = instrument.MasterInstrument.Name;
            lock (QAAdapter._lockDepthSymbol)
            {
                this._marketDepthDataSymbols.Remove(name);
                this._l2Subscriptions.TryRemove(name, out L2Subscription _);
            }
        }

        /// <summary>
        /// Processes a parsed tick data object and updates NinjaTrader with all available market data.
        ///
        /// NOTE: This is the fallback synchronous path. The optimized path uses
        /// OptimizedTickProcessor which handles tick processing asynchronously with
        /// object pooling, backpressure management, and batch processing.
        ///
        /// DEPRECATED: This method is no longer used. All tick processing now goes through
        /// OptimizedTickProcessor which maintains its own shard-local state for thread-safe
        /// volume/price tracking. Kept for API compatibility only.
        /// </summary>
        /// <param name="nativeSymbolName">The native symbol name</param>
        /// <param name="tickData">The parsed tick data</param>
        [Obsolete("Use OptimizedTickProcessor.QueueTick instead. This method is kept for API compatibility only.")]
        public void ProcessParsedTick(string nativeSymbolName, ZerodhaTickData tickData)
        {
            // DEPRECATED: All tick processing now goes through OptimizedTickProcessor
            // This method is kept only for API compatibility
            // The OptimizedTickProcessor maintains shard-local SymbolState for thread-safe
            // volume delta and price change tracking, eliminating the race conditions that
            // occurred when L1Subscription.PreviousVolume/PreviousPrice were shared state.
            Logger.Warn($"[QAAdapter] ProcessParsedTick called for {nativeSymbolName} - this is deprecated, use OptimizedTickProcessor instead");
        }

        // Trading methods - implement these if Zerodha trading is needed
        public void Cancel(NinjaTrader.Cbi.Order[] orders) { }

        public void Change(NinjaTrader.Cbi.Order[] orders) { }

        public void Submit(NinjaTrader.Cbi.Order[] orders) { }

        public void SubscribeAccount(NinjaTrader.Cbi.Account account) { }

        public void UnsubscribeAccount(NinjaTrader.Cbi.Account account) { }

        private int HowManyBarsFromDays(DateTime startDate) => (DateTime.Now - startDate).Days;

        private int HowManyBarsFromMinutes(DateTime startDate)
        {
            return Convert.ToInt32((DateTime.Now - startDate).TotalMinutes);
        }

        private void BarsWorker(QAAdapter.BarsRequest barsRequest)
        {
            if (this._ninjaConnection.Trace.Bars)
                this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.BarsWorker"));

            EventHandler eventHandler = (EventHandler)((s, e) => { });

            try
            {
                
                // Log to file only (not NinjaTrader control panel)
                Logger.Info($"Starting bars request for {barsRequest?.Bars?.Instrument?.MasterInstrument?.Name}");

                if (barsRequest.Progress != null)
                {
                    string shortDatePattern = Globals.GeneralOptions.CurrentCulture.DateTimeFormat.ShortDatePattern;
                    CultureInfo currentCulture = Globals.GeneralOptions.CurrentCulture;
                    barsRequest.Progress.Aborted += eventHandler;
                }

                bool flag = false;
                string name = barsRequest.Bars.Instrument.MasterInstrument.Name;
                //NinjaTrader.Cbi.InstrumentType marketType = barsRequest.Bars.Instrument.GetType();
                MarketType marketType = MarketType.Spot; // Default to Spot market type
                string symbolName = Connector.GetSymbolName(name, out marketType);

                //NinjaTrader.NinjaScript.NinjaScript.Log($"Symbol: {symbolName}, Market Type: {marketType}", NinjaTrader.Cbi.LogLevel.Information);

                // Create the loading UI only if needed
                LoadViewModel loadViewModel = new LoadViewModel();
                loadViewModel.Message = "Loading historical data...";
                loadViewModel.SubMessage = "Preparing request";

                // Make the UI visible regardless of chart grid
                loadViewModel.IsBusy = true;

                List<Record> source = null;


                try
                {
                    //NinjaTrader.NinjaScript.NinjaScript.Log($"Requesting bars: Type={barsRequest.Bars.BarsPeriod.BarsPeriodType}, From={barsRequest.Bars.FromDate}, To={barsRequest.Bars.ToDate}", NinjaTrader.Cbi.LogLevel.Information);
                    Task<List<Record>> task = null;

                    DateTime fromDateWithTime = new DateTime(
                        barsRequest.Bars.FromDate.Year,
                        barsRequest.Bars.FromDate.Month,
                        barsRequest.Bars.FromDate.Day,
                        9, 15, 0);  // 9:15:00 AM IST
                    DateTime toDateWithTime = new DateTime(
                           barsRequest.Bars.ToDate.Year,
                           barsRequest.Bars.ToDate.Month,
                           barsRequest.Bars.ToDate.Day,
                           15, 30, 0);  // 3:30:00 PM IST

                    // Check if this is a synthetic straddle symbol
                    if (SyntheticHistoricalDataService.IsSyntheticSymbol(name))
                    {
                        // Get straddle definition to find CE/PE symbols
                        var straddleDef = _syntheticStraddleService?.GetDefinition(name);
                        if (straddleDef != null)
                        {
                            Logger.Info($"[SYNTH-HIST] Processing synthetic straddle: {name} (CE={straddleDef.CESymbol}, PE={straddleDef.PESymbol})");

                            if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Tick)
                            {
                                // Tick data not supported - return empty list for live accumulation
                                source = new List<Record>();
                                Logger.Info("[SYNTH-HIST] Tick data not supported for synthetic straddles. Using empty history with real-time tick subscription.");
                            }
                            else
                            {
                                // Fetch combined historical data from both legs
                                task = SyntheticHistoricalDataService.Instance.GetSyntheticHistoricalData(
                                    barsRequest.Bars.BarsPeriod.BarsPeriodType,
                                    name,
                                    straddleDef.CESymbol,
                                    straddleDef.PESymbol,
                                    fromDateWithTime,
                                    toDateWithTime,
                                    (ViewModelBase)loadViewModel);
                            }
                        }
                        else
                        {
                            Logger.Warn($"[SYNTH-HIST] No straddle definition found for {name}. Returning empty data.");
                            source = new List<Record>();
                        }
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Day && this.HowManyBarsFromDays(barsRequest.Bars.FromDate) > 0)
                    {
                        task = Connector.Instance.GetHistoricalTrades(barsRequest.Bars.BarsPeriod.BarsPeriodType, symbolName, fromDateWithTime, toDateWithTime,marketType, (ViewModelBase)loadViewModel);
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && this.HowManyBarsFromMinutes(barsRequest.Bars.FromDate) > 0)
                    {
                        task = Connector.Instance.GetHistoricalTrades(barsRequest.Bars.BarsPeriod.BarsPeriodType, symbolName, fromDateWithTime, toDateWithTime, marketType, (ViewModelBase)loadViewModel);
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Tick)
                    {
                        // For tick data, we'll skip historical data requests and just return an empty list
                        // This is expected behavior since Zerodha doesn't support historical tick data
                        source = new List<Record>();
                        Logger.Info("Historical tick data not available from Zerodha. Using empty history with real-time tick subscription.");
                        // No task needed, we already have the empty list
                    }

                    if (task != null)
                    {
                        try
                        {
                            source = task.Result;
                            Logger.Info($"Retrieved {source?.Count ?? 0} historical data points");
                        }
                        catch (AggregateException ae)
                        {
                            // Unwrap aggregate exception to get the real error
                            string errorMsg = ae.InnerException?.Message ?? ae.Message;
                            NinjaTrader.NinjaScript.NinjaScript.Log($"Error retrieving historical data: {errorMsg}", NinjaTrader.Cbi.LogLevel.Error);

                            // Set error flag
                            flag = true;
                        }
                    }
                    else if (source == null)
                    {
                        Logger.Info("No historical data request was made");
                    }
                }
                finally
                {
                    // Clean up UI state
                    loadViewModel.IsBusy = false;
                    loadViewModel.Message = "";
                    loadViewModel.SubMessage = "";
                }

                // Process the data if available
                if (source != null)
                {
                    if (source.Count == 0)
                    {
                        Logger.Info("No data returned from historical data request");
                    }

                    foreach (Record record in (IEnumerable<Record>)source.OrderBy<Record, DateTime>((Func<Record, DateTime>)(x => x.TimeStamp)))
                    {
                        if (barsRequest.Progress != null && barsRequest.Progress.IsAborted)
                        {
                            flag = true;
                            break;
                        }

                        if (this._ninjaConnection.Status != ConnectionStatus.Disconnecting)
                        {
                            if (this._ninjaConnection.Status != ConnectionStatus.Disconnected)
                            {
                                double open = record.Open;
                                double high = record.High;
                                double low = record.Low;
                                double close = record.Close;

                                if (record.Volume >= 0.0)
                                {
                                    long volume = (long)record.Volume;
                                  
                                    TimeZoneInfo indianZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);
                                     DateTime displayTime = TimeZoneInfo.ConvertTime(record.TimeStamp, indianZone);

                                     //Check both date and time constraints
                                     if (displayTime >= barsRequest.Bars.FromDate)
                                     {
                                         //Add time of day filter for market hours
                                        TimeSpan timeOfDay = displayTime.TimeOfDay;

                                        //Only add bars during market hours
                                        if (timeOfDay >= Constants.MarketOpenTime && timeOfDay <= Constants.MarketCloseTime)
                                        {
                                            barsRequest.Bars.Add(open, high, low, close, displayTime, volume, double.MinValue, double.MinValue);
                                        }
                                     }

                                }
                            }
                            else
                                break;
                        }
                        else
                            break;
                    }
                }

                if (barsRequest == null)
                    return;

                if (barsRequest.Progress != null)
                {
                    barsRequest.Progress.Aborted -= eventHandler;
                    barsRequest.Progress.TearDown();
                }

                IBars bars = barsRequest.Bars;
                //NinjaTrader.NinjaScript.NinjaScript.Log("Finishing bars request", NinjaTrader.Cbi.LogLevel.Information);
                barsRequest.BarsCallback(bars, flag ? ErrorCode.UserAbort : ErrorCode.NoError, string.Empty);
                barsRequest = null;
            }
            catch (Exception ex)
            {
                string errorMessage = $"BarsWorker Exception: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $" Inner: {ex.InnerException.Message}";
                }

                NinjaTrader.NinjaScript.NinjaScript.Log(errorMessage, NinjaTrader.Cbi.LogLevel.Error);
                NinjaTrader.NinjaScript.NinjaScript.Log($"Stack trace: {ex.StackTrace}", NinjaTrader.Cbi.LogLevel.Error);

                if (this._ninjaConnection.Trace.Bars)
                    this._ninjaConnection.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.BarsWorker Exception='{ex.ToString()}'"));

                if (barsRequest == null)
                    return;

                if (barsRequest.Progress != null)
                {
                    barsRequest.Progress.Aborted -= eventHandler;
                    barsRequest.Progress.TearDown();
                }

                IBars bars = barsRequest.Bars;
                barsRequest.BarsCallback(bars, ErrorCode.Panic, errorMessage);
            }
        }
        private bool IsIndianMarketInstrument(Instrument instrument)
        {
            // Identify Indian market instruments by exchange or symbol pattern
            string name = instrument.MasterInstrument.Name;
            return name.EndsWith("-NSE") || name.EndsWith("-BSE") ||
                   instrument.Exchange == Exchange.Nse || instrument.Exchange == Exchange.Bse;
        }

        /// <summary>
        /// Checks if a symbol is an index (no volume traded, price updates only).
        /// Index symbols include GIFT NIFTY, NIFTY 50, SENSEX, NIFTY BANK, etc.
        /// </summary>
        private bool IsIndexSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
                return false;

            // Known index symbols that have no volume (they're calculated indices)
            string upperSymbol = symbolName.ToUpperInvariant();
            return upperSymbol == "GIFT_NIFTY" ||
                   upperSymbol == "GIFT NIFTY" ||
                   upperSymbol == "NIFTY 50" ||
                   upperSymbol == "NIFTY" ||  // NT symbol for NIFTY 50 index
                   upperSymbol == "SENSEX" ||
                   upperSymbol == "NIFTY BANK" ||
                   upperSymbol == "BANKNIFTY" ||
                   upperSymbol == "FINNIFTY" ||
                   upperSymbol == "MIDCPNIFTY";
        }

        private NinjaTrader.Gui.Chart.Chart FindChartControl(string instrument)
        {
            foreach (Window window in Globals.AllWindows)
            {
                if (window is NinjaTrader.Gui.Chart.Chart chartControl &&
                    chartControl.ChartTrader?.Instrument?.MasterInstrument?.Name == instrument)
                {
                    return chartControl;
                }
            }
            return null;
        }

        public void RequestBars(
            IBars bars,
            Action<IBars, ErrorCode, string> callback,
            IProgress progress)
        {
            try
            {
                QAAdapter.BarsRequest request = new QAAdapter.BarsRequest()
                {
                    Bars = bars,
                    BarsCallback = callback,
                    Progress = progress
                };
                Task.Run((Action)(() => this.BarsWorker(request)));
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public void OnMarketDepthReceived(Quote quote) { }

        public void RequestHotlistNames(Action<string[], ErrorCode, string> callback) { }

        public void SubscribeHotlist(Hotlist hotlist, Action callback) { }

        public void UnsubscribeHotlist(Hotlist hotlist) { }

        public void SubscribeNews() { }

        public void UnsubscribeNews() { }

        private void SubscribeToSyntheticLeg(string legSymbol)
        {
            try
            {
                // Ensure leg is in subscriptions map so MarketDataService processes it
                Logger.Info($"[SYNTH-LEG] SubscribeToSyntheticLeg called for {legSymbol}");
                if (!_l1Subscriptions.ContainsKey(legSymbol))
                {
                    Logger.Info($"[SYNTH-LEG] Adding local subscription entry for {legSymbol}");
                    var sub = new L1Subscription
                    {
                        IsIndex = false
                    };
                    _l1Subscriptions.TryAdd(legSymbol, sub);

                    // Update Processor Cache for the new leg subscription
                    MarketDataService.Instance.UpdateProcessorSubscriptionCache(this._l1Subscriptions);
                }

                lock (QAAdapter._lockLiveSymbol)
                {
                    // CRITICAL FIX: Always call SubscribeToTicks regardless of local cache.
                    // This allows SubscriptionTrackingService to handle reference counting properly.
                    
                    MarketType mt;
                    string originalSymbol = Connector.GetSymbolName(legSymbol, out mt);

                    // CRITICAL FIX: Detect MCX symbols by checking for CRUDE, GOLD, SILVER, etc.
                    // The GetSymbolName method doesn't properly detect MCX from option symbols like CRUDEOIL26JAN5200CE
                    if (legSymbol.StartsWith("CRUDEOIL", StringComparison.OrdinalIgnoreCase) ||
                        legSymbol.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase) ||
                        legSymbol.StartsWith("SILVER", StringComparison.OrdinalIgnoreCase) ||
                        legSymbol.StartsWith("NATURALGAS", StringComparison.OrdinalIgnoreCase) ||
                        legSymbol.StartsWith("COPPER", StringComparison.OrdinalIgnoreCase))
                    {
                        mt = MarketType.MCX;
                        Logger.Info($"[SYNTH-LEG] Detected MCX commodity symbol: {legSymbol}, forcing MarketType=MCX");
                    }

                    // Always add to local tracking if not present
                    if (!this._marketLiveDataSymbols.Contains(legSymbol))
                    {
                        this._marketLiveDataSymbols.Add(legSymbol);
                    }

                    Logger.Info($"[SYNTH-LEG] Initiating live connection for {legSymbol} (originalSymbol={originalSymbol}, marketType={mt})");

                    // CRITICAL: Capture variables for closure to avoid race conditions
                    string capturedLegSymbol = legSymbol;
                    string capturedOriginalSymbol = originalSymbol;
                    MarketType capturedMarketType = mt;

                    // Use dedicated Thread to completely bypass thread pool starvation
                    var thread = new Thread(() => {
                        try
                        {
                            Logger.Info($"[SYNTH-LEG] Thread STARTED for {capturedLegSymbol}");
                            Connector.Instance.SubscribeToTicks(
                                capturedLegSymbol,
                                capturedMarketType,
                                capturedOriginalSymbol,
                                this._l1Subscriptions,
                                new WebSocketConnectionFunc((Func<bool>)(() =>
                                {
                                    lock (QAAdapter._lockLiveSymbol)
                                        return !this._marketLiveDataSymbols.Contains(capturedLegSymbol);
                                }))
                            ).GetAwaiter().GetResult();
                            Logger.Info($"[SYNTH-LEG] SubscribeToTicks completed for {capturedLegSymbol}");
                        }
                        catch (Exception ex)
                        {
                            lock (QAAdapter._lockLiveSymbol)
                            {
                                if (this._marketLiveDataSymbols.Contains(capturedLegSymbol))
                                    this._marketLiveDataSymbols.Remove(capturedLegSymbol);
                            }
                            Logger.Error($"[SYNTH-LEG] Error subscribing to leg {capturedLegSymbol}: {ex.Message}");
                        }
                    });
                    thread.IsBackground = true;
                    thread.Name = $"WS_LEG_{capturedLegSymbol}";
                    thread.Start();
                    Logger.Info($"[SYNTH-LEG] Thread launched for {capturedLegSymbol}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SYNTH-LEG] Error in SubscribeToSyntheticLeg for {legSymbol}: {ex.Message}");
            }
        }

        public void ProcessSyntheticLegTick(string instrumentSymbol, double price, long volume, DateTime timestamp, SyntheticInstruments.TickType tickType, double bid, double ask)
        {
            if (_syntheticStraddleService != null)
            {
                _syntheticStraddleService.ProcessLegTick(instrumentSymbol, price, volume, timestamp, tickType, bid, ask);
            }
        }

        public void Dispose()
        {
            _syntheticStraddleService?.Dispose();
            this.Disconnect();
            this._l1Subscriptions.Clear();
            this._l2Subscriptions.Clear();
        }

        public void PublishSyntheticTickData(string syntheticSymbol, double price, DateTime timestamp, long volume, SyntheticInstruments.TickType tickType)
        {
            try
            {
                // CRITICAL FIX: Ensure timestamp has proper DateTimeKind for NinjaTrader compatibility
                var ninjaTraderTimestamp = DateTimeHelper.EnsureProperDateTime(timestamp);
                
                lock (this._marketDataLock)
                {
                    if (this._l1Subscriptions.ContainsKey(syntheticSymbol))
                    {
                        var subscription = this._l1Subscriptions[syntheticSymbol];

                        // Use thread-safe snapshot for iteration
                        var callbackSnapshot = subscription.GetCallbacksSnapshot();
                        foreach (var callbackPair in callbackSnapshot)
                        {
                            var callback = callbackPair.Value;
                            try
                            {
                                // Convert TickType to MarketDataType
                                NinjaTrader.Data.MarketDataType marketDataType = NinjaTrader.Data.MarketDataType.Last; // Default
                                switch (tickType)
                                {
                                    case SyntheticInstruments.TickType.Last:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Last;
                                        break;
                                    case SyntheticInstruments.TickType.Bid:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Bid;
                                        break;
                                    case SyntheticInstruments.TickType.Ask:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Ask;
                                        break;
                                }
                                // Invoke the callback with synthetic tick data (using properly formatted timestamp)
                                callback(marketDataType, price, volume, ninjaTraderTimestamp, volume);
                            }
                            catch (Exception ex)
                            {
                                // Check for any DateTime-related conversion errors and suppress them completely
                                if (ex.Message.Contains("DateTime") || 
                                    ex.Message.Contains("sourceTimeZone") || 
                                    ex.Message.Contains("Kind property") ||
                                    ex.Message.Contains("conversion could not be completed"))
                                {
                                    // Completely suppress DateTime conversion errors - they don't affect functionality
                                    continue;
                                }
                                else
                                {
                                    Logger.Debug($"QAAdapter: Non-DateTime error invoking callback for {syntheticSymbol}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.Debug($"QAAdapter: No subscriptions found for synthetic symbol {syntheticSymbol}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Only log non-DateTime errors to avoid spamming logs
                if (!ex.Message.Contains("DateTime") && 
                    !ex.Message.Contains("sourceTimeZone") && 
                    !ex.Message.Contains("Kind property") &&
                    !ex.Message.Contains("conversion could not be completed"))
                {
                    Logger.Error($"QAAdapter: Error in PublishSyntheticTickData for {syntheticSymbol}: {ex.Message}");
                }
            }
        }

        // Note: EnsureProperDateTime has been extracted to DateTimeHelper class

        private class BarsRequest
        {
            public IBars Bars { get; set; }

            public Action<IBars, ErrorCode, string> BarsCallback { get; set; }

            public IProgress Progress { get; set; }
        }
    }
}
