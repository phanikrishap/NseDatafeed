using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System;
using System.Globalization;
using System.Threading;
using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Zerodha.Websockets;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.MarketData;

#nullable disable
namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// ZerodhaAdapter partial class - Market Data Subscriptions
    /// Handles L1/L2 market data subscription and unsubscription
    /// </summary>
    public partial class ZerodhaAdapter
    {
        public void SubscribeMarketData(
            Instrument instrument,
            Action<MarketDataType, double, long, DateTime, long> callback)
        {
            try
            {
                if (this._zerodhaConncetion.Trace.MarketData)
                    this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                        $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketData: instrument='{instrument.FullName}'"));

                if (this._zerodhaConncetion.Status == ConnectionStatus.Disconnecting ||
                    this._zerodhaConncetion.Status == ConnectionStatus.Disconnected)
                    return;

                string name = instrument.MasterInstrument.Name;
                if (string.IsNullOrEmpty(name))
                    return;

                // Handle Synthetic Straddle Subscription
                if (_syntheticStraddleService != null && _syntheticStraddleService.IsSyntheticSymbol(name))
                {
                    SubscribeSyntheticMarketData(instrument, name, callback);
                    return;
                }

                string nativeSymbolName = name;

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

                    Logger.Info($"[ZerodhaAdapter] SubscribeMarketData: NEW subscription for '{nativeSymbolName}', callbacks={l1Subscription.CallbackCount}, instrument={instrument.FullName}");

                    if (isIndex)
                        Logger.Info($"ZerodhaAdapter.SubscribeMarketData: Subscribed to INDEX symbol '{nativeSymbolName}' (price updates without volume)");
                }
                else
                {
                    // CRITICAL: AddCallback always adds a new callback (supports multiple subscribers)
                    int beforeCount = l1Subscription.CallbackCount;
                    l1Subscription.AddCallback(instrument, callback);
                    Logger.Info($"[ZerodhaAdapter] SubscribeMarketData: ADDED callback for '{nativeSymbolName}', callbacks={beforeCount}->{l1Subscription.CallbackCount}, instrument={instrument.FullName}");
                }

                // Update OptimizedTickProcessor cache immediately
                MarketDataService.Instance.UpdateProcessorSubscriptionCache(this._l1Subscriptions);

                // Now handle the websocket subscription
                InitiateWebSocketSubscription(name, nativeSymbolName);
            }
            catch (Exception ex)
            {
                if (this._zerodhaConncetion.Trace.Connect)
                    this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                        $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketData Exception={ex}"));
            }
        }

        private void SubscribeSyntheticMarketData(Instrument instrument, string name, Action<MarketDataType, double, long, DateTime, long> callback)
        {
            // Register subscription for SYNTHETIC symbol (parent)
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

            // Subscribe to legs
            var def = _syntheticStraddleService.GetDefinition(name);
            if (def != null)
            {
                Logger.Debug($"ZerodhaAdapter: Subscribing to synthetic straddle {name} (Legs: {def.CESymbol}, {def.PESymbol})");
                SubscribeToSyntheticLeg(def.CESymbol);
                SubscribeToSyntheticLeg(def.PESymbol);
            }
            else
            {
                Logger.Error($"ZerodhaAdapter: Straddle definition is NULL for {name}!");
            }
        }

        private void InitiateWebSocketSubscription(string name, string nativeSymbolName)
        {
            lock (ZerodhaAdapter._lockLiveSymbol)
            {
                MarketType mt;
                string originalSymbol = Connector.GetSymbolName(name, out mt);

                // Detect MCX symbols
                if (SymbolHelper.IsMcxSymbol(name))
                {
                    mt = MarketType.MCX;
                    Logger.Debug($"[SUBSCRIBE] Detected MCX commodity symbol: {name}, forcing MarketType=MCX");
                }

                // Always add to local tracking if not present
                if (!this._marketLiveDataSymbols.Contains(nativeSymbolName))
                {
                    this._marketLiveDataSymbols.Add(nativeSymbolName);
                }

                Logger.Debug($"[SUBSCRIBE] Initiating WebSocket for {name} (originalSymbol={originalSymbol}, marketType={mt})");

                // Capture variables for closure
                string capturedName = name;
                string capturedNativeSymbol = nativeSymbolName;
                string capturedOriginalSymbol = originalSymbol;
                MarketType capturedMarketType = mt;

                // Use dedicated Thread to bypass thread pool starvation
                var thread = new Thread(() =>
                {
                    try
                    {
                        Logger.Debug($"[SUBSCRIBE] Thread STARTED for {capturedName}");
                        Connector.Instance.SubscribeToTicks(
                            capturedNativeSymbol,
                            capturedMarketType,
                            capturedOriginalSymbol,
                            this._l1Subscriptions,
                            new WebSocketConnectionFunc((Func<bool>)(() =>
                            {
                                lock (ZerodhaAdapter._lockLiveSymbol)
                                    return !this._marketLiveDataSymbols.Contains(capturedNativeSymbol);
                            }))
                        ).GetAwaiter().GetResult();
                        Logger.Debug($"[SUBSCRIBE] SubscribeToTicks completed for {capturedName}");
                    }
                    catch (Exception ex)
                    {
                        lock (ZerodhaAdapter._lockLiveSymbol)
                        {
                            if (this._marketLiveDataSymbols.Contains(capturedNativeSymbol))
                                this._marketLiveDataSymbols.Remove(capturedNativeSymbol);
                        }
                        Logger.Error($"[SUBSCRIBE] Error subscribing to {capturedName}: {ex.Message}");

                        if (this._zerodhaConncetion.Trace.Connect)
                            this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture,
                                $"({this._options.Name}) ZerodhaAdapter.SubscribeToTicks Exception={ex}"));
                    }
                });
                thread.IsBackground = true;
                thread.Name = $"WS_{capturedName}";
                thread.Start();
                Logger.Debug($"[SUBSCRIBE] Thread launched for {capturedName}, ThreadId={thread.ManagedThreadId}");
            }
        }

        public void UnsubscribeMarketData(Instrument instrument)
        {
            string name = instrument.MasterInstrument.Name;

            // STICKY SUBSCRIPTION: Once subscribed, keep receiving data.
            // NinjaTrader aggressively calls UnsubscribeMarketData during BarsRequest.
            // We only log the unsubscribe attempt but DO NOT remove the callback.

            if (_l1Subscriptions.TryGetValue(name, out var subscription))
            {
                if (subscription.ContainsCallback(instrument))
                {
                    Logger.Info($"[UNSUBSCRIBE] [Instance={_instanceId}] IGNORED unsubscribe request for {name} (keeping callback alive), total callbacks: {subscription.CallbackCount}");
                }
                else
                {
                    Logger.Info($"[UNSUBSCRIBE] [Instance={_instanceId}] No callback found for {name} (instrument={instrument.FullName}), total callbacks: {subscription.CallbackCount}");
                }
            }
        }

        public void SubscribeMarketDepth(
            Instrument instrument,
            Action<int, string, Operation, MarketDataType, double, long, DateTime> callback)
        {
            try
            {
                if (this._zerodhaConncetion.Trace.MarketDepth)
                    this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketDepth: instrument='{instrument.FullName}'"));
                if (this._zerodhaConncetion.Status == ConnectionStatus.Disconnecting || this._zerodhaConncetion.Status == ConnectionStatus.Disconnected)
                    return;
                string name = instrument.MasterInstrument.Name;
                MarketType mt = MarketType.Spot;
                if (string.IsNullOrEmpty(name))
                    return;
                string nativeSymbolName = name;
                lock (ZerodhaAdapter._lockDepthSymbol)
                {
                    if (!this._marketDepthDataSymbols.Contains(name))
                    {
                        string originalSymbol = Connector.GetSymbolName(name, out mt);
                        this._marketDepthDataSymbols.Add(nativeSymbolName);
                        System.Threading.Tasks.Task.Run((Func<System.Threading.Tasks.Task>)(() => Connector.Instance.SubscribeToDepth(nativeSymbolName, mt, originalSymbol, this._l2Subscriptions, new WebSocketConnectionFunc((Func<bool>)(() =>
                        {
                            lock (ZerodhaAdapter._lockDepthSymbol)
                                return !this._marketDepthDataSymbols.Contains(nativeSymbolName);
                        })))));
                    }
                }
                L2Subscription l2Subscription1;
                this._l2Subscriptions.TryGetValue(name, out l2Subscription1);
                if (l2Subscription1 == null)
                {
                    var l2Subscriptions = this._l2Subscriptions;
                    string key = name;
                    var l2Subscription2 = new L2Subscription();
                    l2Subscription2.Instrument = instrument;
                    l2Subscription1 = l2Subscription2;
                    l2Subscriptions.TryAdd(key, l2Subscription2);
                }
                l2Subscription1.L2Callbacks = new System.Collections.Generic.SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>((System.Collections.Generic.IDictionary<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>)l2Subscription1.L2Callbacks)
                {
                    {
                        instrument,
                        callback
                    }
                };
                int status = (int)this._zerodhaConncetion.Status;
            }
            catch (Exception ex)
            {
                if (!this._zerodhaConncetion.Trace.Connect)
                    return;
                this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.SubscribeMarketDepth Exception={ex.ToString()}"));
            }
        }

        public void UnsubscribeMarketDepth(Instrument instrument)
        {
            string name = instrument.MasterInstrument.Name;
            lock (ZerodhaAdapter._lockDepthSymbol)
            {
                this._marketDepthDataSymbols.Remove(name);
                this._l2Subscriptions.TryRemove(name, out L2Subscription _);
            }
        }
    }
}
