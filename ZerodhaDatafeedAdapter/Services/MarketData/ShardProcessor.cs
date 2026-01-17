using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Data.SQLite;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Classes;
using NinjaTrader.Data;
using NinjaTrader.Cbi;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Handles the core tick processing logic for a shard.
    /// Manages its own shard-local state to avoid lock contention.
    /// </summary>
    public class ShardProcessor
    {
        private readonly int _shardIndex;
        private readonly Shard _shard;
        private readonly TickCacheManager _cacheManager;
        private readonly TickSubscriptionRegistry _subscriptionRegistry;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly IObserver<TickStreamItem> _tickObserver;
        private readonly Action<string, double> _optionTickReceived;
        private readonly TimeZoneInfo _istTimeZone;

        // Diagnostic counters
        private long _ticksProcessed = 0;
        private long _callbacksExecuted = 0;
        private long _callbackErrors = 0;
        private long _totalCallbackTimeMs = 0;
        private long _slowCallbacks = 0;
        private long _verySlowCallbacks = 0;
        private long _noCallbackLogCounter = 0;

        public long TicksProcessed => Interlocked.Read(ref _ticksProcessed);
        public long CallbacksExecuted => Interlocked.Read(ref _callbacksExecuted);
        public long CallbackErrors => Interlocked.Read(ref _callbackErrors);
        public long TotalCallbackTimeMs => Interlocked.Read(ref _totalCallbackTimeMs);
        public long SlowCallbacks => Interlocked.Read(ref _slowCallbacks);
        public long VerySlowCallbacks => Interlocked.Read(ref _verySlowCallbacks);

        public ShardProcessor(
            int index,
            Shard shard,
            TickCacheManager cacheManager,
            TickSubscriptionRegistry subRegistry,
            PerformanceMonitor perfMonitor,
            IObserver<TickStreamItem> tickObserver,
            Action<string, double> optionTickReceived)
        {
            _shardIndex = index;
            _shard = shard;
            _cacheManager = cacheManager;
            _subscriptionRegistry = subRegistry;
            _performanceMonitor = perfMonitor;
            _tickObserver = tickObserver;
            _optionTickReceived = optionTickReceived;
            _istTimeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);
        }

        public void ProcessLoop(CancellationToken token)
        {
            Logger.Debug($"[Shard-{_shardIndex}] Starting worker loop");
            
            try
            {
                foreach (var item in _shard.Queue.GetConsumingEnumerable(token))
                {
                    if (item?.TickData != null)
                    {
                        ProcessSingleTick(item);
                    }
                    Interlocked.Increment(ref _ticksProcessed);
                }
            }
            catch (OperationCanceledException) { /* Normal shutdown */ }
            catch (Exception ex)
            {
                Logger.Error($"[Shard-{_shardIndex}] CRITICAL FAILURE: {ex.Message}", ex);
            }
        }

        private void ProcessSingleTick(TickProcessingItem item)
        {
            var sw = Stopwatch.StartNew();
            string ntSymbolName = item.NativeSymbolName;

            try
            {
                // 1. Always cache the tick (Critical for pre/post market)
                if (item.TickData.LastTradePrice > 0)
                {
                    _cacheManager.CacheLastTick(ntSymbolName, item.TickData, _tickObserver, _optionTickReceived);
                }

                // 2. Check for subscription
                if (!_subscriptionRegistry.Subscriptions.TryGetValue(ntSymbolName, out var subscription))
                {
                    return;
                }

                // 3. Check for callbacks
                if (!_subscriptionRegistry.Callbacks.TryGetValue(ntSymbolName, out var callbacks) || callbacks.Count == 0)
                {
                    TrackNoCallback(ntSymbolName);
                    return;
                }

                // 4. Shard-local state for volume/price tracking
                var symbolState = _shard.GetOrCreateState(ntSymbolName);
                int volumeDelta = CalculateVolumeDelta(symbolState, item.TickData);
                bool isIndex = subscription.IsIndex;
                bool priceChanged = isIndex && Math.Abs(item.TickData.LastTradePrice - symbolState.PreviousPrice) > 0.0001;
                symbolState.PreviousPrice = item.TickData.LastTradePrice;

                // 5. Fire callbacks
                DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, _istTimeZone);
                bool isOutsideMarket = !DateTimeHelper.IsMarketOpenForSymbol(ntSymbolName);
                bool shouldFire = (volumeDelta > 0) || (isIndex && priceChanged) || isOutsideMarket;

                if (shouldFire && item.TickData.LastTradePrice > 0)
                {
                    foreach (var cbInfo in callbacks)
                    {
                        FireCallbackWithTiming(cbInfo, item.TickData, volumeDelta, now, subscription);
                    }
                }

                // 6. Market Depth
                if (item.TickData.HasMarketDepth)
                {
                    ProcessMarketDepth(item.TickData, now);
                }

                sw.Stop();
                _performanceMonitor.RecordTickProcessed(ntSymbolName, sw.ElapsedMilliseconds);
                _performanceMonitor.RecordProcessingLatency(ntSymbolName, DateTime.UtcNow - item.QueueTime);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Shard-{_shardIndex}] Error processing {ntSymbolName}: {ex.Message}");
            }
        }

        private void FireCallbackWithTiming(SubscriptionCallback cbInfo, ZerodhaTickData tick, int volDelta, DateTime now, L1Subscription sub)
        {
            try
            {
                if (cbInfo?.Callback == null || cbInfo.Instrument?.MasterInstrument == null) return;

                var sw = Stopwatch.StartNew();
                cbInfo.Callback(MarketDataType.Last, tick.LastTradePrice, Math.Max(1, volDelta), now, 0L);
                sw.Stop();

                TrackTiming(sw.ElapsedMilliseconds);

                if (tick.BuyPrice > 0) cbInfo.Callback(MarketDataType.Bid, tick.BuyPrice, tick.BuyQty, now, 0L);
                if (tick.SellPrice > 0) cbInfo.Callback(MarketDataType.Ask, tick.SellPrice, tick.SellQty, now, 0L);
                
                // Common Daily Data
                if (tick.TotalQtyTraded > 0) cbInfo.Callback(MarketDataType.DailyVolume, tick.TotalQtyTraded, tick.TotalQtyTraded, now, 0L);
                if (tick.High > 0) cbInfo.Callback(MarketDataType.DailyHigh, tick.High, 0L, now, 0L);
                if (tick.Low > 0) cbInfo.Callback(MarketDataType.DailyLow, tick.Low, 0L, now, 0L);
                if (tick.Open > 0) cbInfo.Callback(MarketDataType.Opening, tick.Open, 0L, now, 0L);
                if (tick.Close > 0) cbInfo.Callback(MarketDataType.LastClose, tick.Close, 0L, now, 0L);
                if (tick.OpenInterest > 0) cbInfo.Callback(MarketDataType.OpenInterest, tick.OpenInterest, tick.OpenInterest, now, 0L);

                Interlocked.Increment(ref _callbacksExecuted);
            }
            catch { Interlocked.Increment(ref _callbackErrors); }
        }

        private int CalculateVolumeDelta(SymbolState state, ZerodhaTickData tick)
        {
            int delta = state.PreviousVolume > 0 ? Math.Max(0, tick.TotalQtyTraded - state.PreviousVolume) : (tick.LastTradeQty > 0 ? tick.LastTradeQty : 0);
            state.PreviousVolume = tick.TotalQtyTraded;
            state.LastTickTime = DateTime.UtcNow;
            return delta;
        }

        private void ProcessMarketDepth(ZerodhaTickData tick, DateTime now)
        {
            if (!_subscriptionRegistry.L2Subscriptions.TryGetValue(tick.InstrumentIdentifier, out var l2Sub)) return;

            foreach (var callbackKvp in l2Sub.L2Callbacks)
            {
                var instrument = callbackKvp.Key;
                var callback = callbackKvp.Value;

                if (tick.AskDepth != null)
                {
                    foreach (var ask in tick.AskDepth)
                        if (ask?.Quantity > 0) instrument.UpdateMarketDepth(MarketDataType.Ask, ask.Price, ask.Quantity, Operation.Update, now, callback);
                }
                if (tick.BidDepth != null)
                {
                    foreach (var bid in tick.BidDepth)
                        if (bid?.Quantity > 0) instrument.UpdateMarketDepth(MarketDataType.Bid, bid.Price, bid.Quantity, Operation.Update, now, callback);
                }
            }
        }

        private void TrackTiming(long ms)
        {
            Interlocked.Add(ref _totalCallbackTimeMs, ms);
            if (ms > 5) Interlocked.Increment(ref _verySlowCallbacks);
            else if (ms > 1) Interlocked.Increment(ref _slowCallbacks);
        }

        private void TrackNoCallback(string sym)
        {
            if ((sym.Contains("CE") || sym.Contains("PE")) && !sym.Contains("SENSEX"))
            {
                long count = Interlocked.Increment(ref _noCallbackLogCounter);
                if (count <= 20 || count % 100 == 1) // Constants.TickSize
                    Logger.Info($"[Shard-{_shardIndex}] Subscription exists but NO CALLBACKS for {sym}");
            }
        }
    }

    // Helper class for Shard state (temporary until moved)
    public class Shard
    {
        public readonly BlockingCollection<TickProcessingItem> Queue;
        public Task WorkerTask;
        public readonly Dictionary<string, SymbolState> SymbolStates = new Dictionary<string, SymbolState>();

        public Shard(int capacity)
        {
            Queue = new BlockingCollection<TickProcessingItem>(capacity);
        }

        public SymbolState GetOrCreateState(string symbol)
        {
            if (!SymbolStates.TryGetValue(symbol, out var state))
            {
                state = new SymbolState();
                SymbolStates[symbol] = state;
            }
            return state;
        }
    }
}
