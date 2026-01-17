using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Threading;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;
using NinjaTrader.Core;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Manages the tick cache for replaying ticks to late-registered callbacks.
    /// Critical for post/pre-market data consistency.
    /// </summary>
    public class TickCacheManager
    {
        private readonly ConcurrentDictionary<string, ZerodhaTickData> _lastTickCache = new ConcurrentDictionary<string, ZerodhaTickData>();
        private readonly ConcurrentDictionary<string, bool> _initializedCallbacks = new ConcurrentDictionary<string, bool>();
        private readonly int _maxTickCacheSize;
        private readonly TimeZoneInfo _istTimeZone;
        private long _tickCacheHits = 0;
        private long _tickCacheMisses = 0;
        private long _cacheLastTickLogCounter = 0;

        public long CacheHits => Interlocked.Read(ref _tickCacheHits);
        public long CacheMisses => Interlocked.Read(ref _tickCacheMisses);

        public TickCacheManager(int maxCacheSize = 1000)
        {
            _maxTickCacheSize = maxCacheSize;
            _istTimeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);
        }

        public void CacheLastTick(string symbol, ZerodhaTickData tickData, IObserver<TickStreamItem> tickObserver, Action<string, double> optionTickReceived)
        {
            if (string.IsNullOrEmpty(symbol) || tickData == null || tickData.LastTradePrice <= 0)
                return;

            // Diagnostic logging for options
            if ((symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX"))
            {
                long count = Interlocked.Increment(ref _cacheLastTickLogCounter);
                if (count <= 20)
                {
                    Logger.Info($"[TCM-CACHE-ENTRY] #{count} CacheLastTick for '{symbol}': LTP={tickData.LastTradePrice}");
                }
            }

            // Create a copy to avoid reference leaks from pooled objects
            var cachedTick = CloneTick(tickData);
            _lastTickCache[symbol] = cachedTick;

            // Publish to reactive stream
            try
            {
                DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, _istTimeZone);
                tickObserver.OnNext(new TickStreamItem(symbol, tickData, now));
            }
            catch (Exception ex)
            {
                Logger.Debug($"[TCM-RX] Error publishing for {symbol}: {ex.Message}");
            }

            // Legacy event callback
            if ((symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX"))
            {
                try
                {
                    optionTickReceived?.Invoke(symbol, tickData.LastTradePrice);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[TCM-EVENT] Error firing OptionTickReceived for {symbol}: {ex.Message}");
                }
            }

            TrimCacheIfNeeded();
        }

        private ZerodhaTickData CloneTick(ZerodhaTickData tickData)
        {
            var cachedTick = new ZerodhaTickData
            {
                InstrumentIdentifier = tickData.InstrumentIdentifier,
                InstrumentToken = tickData.InstrumentToken, // Retained original line
                LastTradePrice = tickData.LastTradePrice,
                LastTradeQty = tickData.LastTradeQty,
                TotalQtyTraded = tickData.TotalQtyTraded,
                BuyQty = tickData.BuyQty,
                SellQty = tickData.SellQty,
                Open = tickData.Open,
                High = tickData.High,
                Low = tickData.Low,
                Close = tickData.Close,
                OpenInterest = tickData.OpenInterest,
                ExchangeTimestamp = tickData.ExchangeTimestamp
            };

            if (tickData.BidDepth != null && tickData.BidDepth.Length > 0 && tickData.BidDepth[0] != null)
            {
                cachedTick.BidDepth[0] = new Models.DepthEntry { Price = tickData.BidDepth[0].Price, Quantity = tickData.BidDepth[0].Quantity };
            }
            if (tickData.AskDepth != null && tickData.AskDepth.Length > 0 && tickData.AskDepth[0] != null)
            {
                cachedTick.AskDepth[0] = new Models.DepthEntry { Price = tickData.AskDepth[0].Price, Quantity = tickData.AskDepth[0].Quantity };
            }
            return cachedTick;
        }

        public void TryImmediateReplay(string symbol, ZerodhaTickData cachedTick, IReadOnlyDictionary<string, List<SubscriptionCallback>> callbackCache)
        {
            if (string.IsNullOrEmpty(symbol) || cachedTick == null || cachedTick.LastTradePrice <= 0)
                return;

            if (!callbackCache.TryGetValue(symbol, out var callbacks) || callbacks == null || callbacks.Count == 0)
                return;

            DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, _istTimeZone);
            int firedCount = 0;

            foreach (var callbackInfo in callbacks)
            {
                if (callbackInfo?.Callback == null || callbackInfo.Instrument?.MasterInstrument == null)
                    continue;

                string callbackKey = $"{symbol}_{callbackInfo.Callback.GetHashCode()}";
                if (!_initializedCallbacks.TryAdd(callbackKey, true))
                    continue;

                FireCallback(callbackInfo.Callback, cachedTick, now);
                firedCount++;
                Interlocked.Increment(ref _tickCacheHits);
            }

            if (firedCount > 0 && (symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX"))
            {
                Logger.Info($"[TCM-IMMED-REPLAY] Replayed {symbol} to {firedCount} callbacks");
            }
        }

        public void ReplayForNewCallbacks(IReadOnlyDictionary<string, List<SubscriptionCallback>> newCallbackCache)
        {
            if (newCallbackCache == null || newCallbackCache.Count == 0 || _lastTickCache.Count == 0)
                return;

            DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, _istTimeZone);
            int callbacksFired = 0;

            foreach (var kvp in newCallbackCache)
            {
                string symbol = kvp.Key;
                if (!_lastTickCache.TryGetValue(symbol, out var cachedTick) || cachedTick.LastTradePrice <= 0)
                {
                    Interlocked.Increment(ref _tickCacheMisses);
                    continue;
                }

                foreach (var callbackInfo in kvp.Value)
                {
                    if (callbackInfo?.Callback == null || callbackInfo.Instrument?.MasterInstrument == null)
                        continue;

                    string callbackKey = $"{symbol}_{callbackInfo.Callback.GetHashCode()}";
                    if (!_initializedCallbacks.TryAdd(callbackKey, true))
                        continue;

                    FireCallback(callbackInfo.Callback, cachedTick, now);
                    callbacksFired++;
                    Interlocked.Increment(ref _tickCacheHits);
                }
            }

            if (callbacksFired > 0)
                Logger.Info($"[TCM-REPLAY] Batch replayed {callbacksFired} callbacks from cache");
        }

        private void FireCallback(Action<NinjaTrader.Data.MarketDataType, double, long, DateTime, long> callback, ZerodhaTickData tick, DateTime time)
        {
            callback(NinjaTrader.Data.MarketDataType.Last, tick.LastTradePrice, Math.Max(1, tick.LastTradeQty), time, 0L);
            if (tick.BuyPrice > 0) callback(NinjaTrader.Data.MarketDataType.Bid, tick.BuyPrice, tick.BuyQty, time, 0L);
            if (tick.SellPrice > 0) callback(NinjaTrader.Data.MarketDataType.Ask, tick.SellPrice, tick.SellQty, time, 0L);
            if (tick.TotalQtyTraded > 0) callback(NinjaTrader.Data.MarketDataType.DailyVolume, tick.TotalQtyTraded, tick.TotalQtyTraded, time, 0L);
            if (tick.High > 0) callback(NinjaTrader.Data.MarketDataType.DailyHigh, tick.High, 0L, time, 0L);
            if (tick.Low > 0) callback(NinjaTrader.Data.MarketDataType.DailyLow, tick.Low, 0L, time, 0L);
            if (tick.OpenInterest > 0) callback(NinjaTrader.Data.MarketDataType.OpenInterest, tick.OpenInterest, tick.OpenInterest, time, 0L);
        }

        private void TrimCacheIfNeeded()
        {
            if (_lastTickCache.Count > _maxTickCacheSize)
            {
                int toRemove = _lastTickCache.Count - _maxTickCacheSize + 100;
                var keysToRemove = _lastTickCache.Keys.Take(toRemove).ToList();
                foreach (var key in keysToRemove) _lastTickCache.TryRemove(key, out _);
                Logger.Debug($"[TCM] Trimmed cache to {_lastTickCache.Count}");
            }
        }

        public bool TryGetLastTick(string symbol, out ZerodhaTickData tick) => _lastTickCache.TryGetValue(symbol, out tick);
    }
}
