using NinjaTrader.Cbi;
using System;
using System.Threading.Tasks;
using ZerodhaAPI.Common.Enums;
using ZerodhaAPI.Zerodha.Websockets;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.SyntheticInstruments;

#nullable disable
namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// ZerodhaAdapter partial class - Synthetic Straddle Handling
    /// Handles synthetic instrument subscriptions and tick publishing
    /// </summary>
    public partial class ZerodhaAdapter
    {
        private void SubscribeToSyntheticLeg(string legSymbol)
        {
            try
            {
                Logger.Debug($"[SYNTH-LEG] SubscribeToSyntheticLeg called for {legSymbol}");
                if (!_l1Subscriptions.ContainsKey(legSymbol))
                {
                    Logger.Debug($"[SYNTH-LEG] Adding local subscription entry for {legSymbol}");
                    var sub = new L1Subscription
                    {
                        IsIndex = false
                    };
                    _l1Subscriptions.TryAdd(legSymbol, sub);

                    // Update Processor Cache for the new leg subscription
                    MarketDataService.Instance.UpdateProcessorSubscriptionCache(this._l1Subscriptions);
                }

                lock (ZerodhaAdapter._lockLiveSymbol)
                {
                    MarketType mt;
                    string originalSymbol = Connector.GetSymbolName(legSymbol, out mt);

                    // Detect MCX symbols
                    if (SymbolHelper.IsMcxSymbol(legSymbol))
                    {
                        mt = MarketType.MCX;
                        Logger.Debug($"[SYNTH-LEG] Detected MCX commodity symbol: {legSymbol}, forcing MarketType=MCX");
                    }

                    // Always add to local tracking if not present
                    if (!this._marketLiveDataSymbols.Contains(legSymbol))
                    {
                        this._marketLiveDataSymbols.Add(legSymbol);
                    }

                    Logger.Debug($"[SYNTH-LEG] Initiating live connection for {legSymbol} (originalSymbol={originalSymbol}, marketType={mt})");

                    // Capture variables for closure
                    string capturedLegSymbol = legSymbol;
                    string capturedOriginalSymbol = originalSymbol;
                    MarketType capturedMarketType = mt;

                    // Use Task.Run for async WebSocket subscription
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Debug($"[SYNTH-LEG] Task STARTED for {capturedLegSymbol}");
                            await Connector.Instance.SubscribeToTicks(
                                capturedLegSymbol,
                                capturedMarketType,
                                capturedOriginalSymbol,
                                this._l1Subscriptions,
                                new WebSocketConnectionFunc((Func<bool>)(() =>
                                {
                                    lock (ZerodhaAdapter._lockLiveSymbol)
                                        return !this._marketLiveDataSymbols.Contains(capturedLegSymbol);
                                }))
                            );
                            Logger.Debug($"[SYNTH-LEG] SubscribeToTicks completed for {capturedLegSymbol}");
                        }
                        catch (Exception ex)
                        {
                            lock (ZerodhaAdapter._lockLiveSymbol)
                            {
                                if (this._marketLiveDataSymbols.Contains(capturedLegSymbol))
                                    this._marketLiveDataSymbols.Remove(capturedLegSymbol);
                            }
                            Logger.Error($"[SYNTH-LEG] Error subscribing to leg {capturedLegSymbol}: {ex.Message}");
                        }
                    });
                    Logger.Debug($"[SYNTH-LEG] Task launched for {capturedLegSymbol}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SYNTH-LEG] Error in SubscribeToSyntheticLeg for {legSymbol}: {ex.Message}");
            }
        }

        public void ProcessSyntheticLegTick(string instrumentSymbol, double price, long volume, DateTime timestamp, TickType tickType, double bid, double ask)
        {
            if (_syntheticStraddleService != null)
            {
                _syntheticStraddleService.ProcessLegTick(instrumentSymbol, price, volume, timestamp, tickType, bid, ask);
            }
        }

        public void PublishSyntheticTickData(string syntheticSymbol, double price, DateTime timestamp, long volume, TickType tickType)
        {
            try
            {
                // Ensure timestamp has proper DateTimeKind for NinjaTrader compatibility
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
                                NinjaTrader.Data.MarketDataType marketDataType = NinjaTrader.Data.MarketDataType.Last;
                                switch (tickType)
                                {
                                    case TickType.Last:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Last;
                                        break;
                                    case TickType.Bid:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Bid;
                                        break;
                                    case TickType.Ask:
                                        marketDataType = NinjaTrader.Data.MarketDataType.Ask;
                                        break;
                                }
                                // Invoke the callback with synthetic tick data
                                callback(marketDataType, price, volume, ninjaTraderTimestamp, volume);
                            }
                            catch (Exception ex)
                            {
                                // Suppress DateTime conversion errors
                                if (ex.Message.Contains("DateTime") ||
                                    ex.Message.Contains("sourceTimeZone") ||
                                    ex.Message.Contains("Kind property") ||
                                    ex.Message.Contains("conversion could not be completed"))
                                {
                                    continue;
                                }
                                else
                                {
                                    Logger.Debug($"ZerodhaAdapter: Non-DateTime error invoking callback for {syntheticSymbol}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Logger.Debug($"ZerodhaAdapter: No subscriptions found for synthetic symbol {syntheticSymbol}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Only log non-DateTime errors
                if (!ex.Message.Contains("DateTime") &&
                    !ex.Message.Contains("sourceTimeZone") &&
                    !ex.Message.Contains("Kind property") &&
                    !ex.Message.Contains("conversion could not be completed"))
                {
                    Logger.Error($"ZerodhaAdapter: Error in PublishSyntheticTickData for {syntheticSymbol}: {ex.Message}");
                }
            }
        }
    }
}
