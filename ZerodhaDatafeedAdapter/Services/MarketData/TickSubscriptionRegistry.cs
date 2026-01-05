using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Manages instrument subscriptions and callback registrations.
    /// Uses atomic swap pattern for thread-safe cache updates.
    /// </summary>
    public class TickSubscriptionRegistry
    {
        private volatile ConcurrentDictionary<string, L1Subscription> _subscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
        private volatile ConcurrentDictionary<string, L2Subscription> _l2SubscriptionCache = new ConcurrentDictionary<string, L2Subscription>();
        private volatile ConcurrentDictionary<string, List<SubscriptionCallback>> _callbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();
        
        private readonly ConcurrentDictionary<string, DateTime> _uninitializedSymbols = new ConcurrentDictionary<string, DateTime>();
        private readonly object _cacheUpdateLock = new object();
        private const int MAX_UNINIT_RETRY_SECONDS = 30;

        public ConcurrentDictionary<string, L1Subscription> Subscriptions => _subscriptionCache;
        public ConcurrentDictionary<string, L2Subscription> L2Subscriptions => _l2SubscriptionCache;
        public ConcurrentDictionary<string, List<SubscriptionCallback>> Callbacks => _callbackCache;
        public int PendingInitializationCount => _uninitializedSymbols.Count;

        public (ConcurrentDictionary<string, L1Subscription> subs, ConcurrentDictionary<string, List<SubscriptionCallback>> callbacks) 
            UpdateSubscriptions(ConcurrentDictionary<string, L1Subscription> subscriptions, bool isUnderMemoryPressure)
        {
            if (subscriptions == null || subscriptions.Count == 0)
            {
                Logger.Warn("[TSR] UpdateSubscriptions called with empty/null - skipping");
                return (_subscriptionCache, _callbackCache);
            }

            var newSubscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
            var newCallbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();

            int maxCacheSize = isUnderMemoryPressure ? 500 : 2000;
            int itemsProcessed = 0;
            int skippedUninitializedCount = 0;

            foreach (var kvp in subscriptions)
            {
                if (itemsProcessed >= maxCacheSize) break;

                newSubscriptionCache[kvp.Key] = kvp.Value;
                var callbacks = new List<SubscriptionCallback>();
                var callbackSnapshot = kvp.Value.GetCallbacksSnapshot();

                foreach (var callbackPair in callbackSnapshot)
                {
                    var instrument = callbackPair.Key;
                    if (instrument?.MasterInstrument == null)
                    {
                        skippedUninitializedCount++;
                        if (!_uninitializedSymbols.ContainsKey(kvp.Key))
                        {
                            _uninitializedSymbols[kvp.Key] = DateTime.UtcNow;
                            Logger.Warn($"[TSR] Skipping uninitialized instrument for {kvp.Key}");
                        }
                        continue;
                    }

                    callbacks.Add(new SubscriptionCallback { Instrument = instrument, Callback = callbackPair.Value });
                    _uninitializedSymbols.TryRemove(kvp.Key, out _);
                }

                if (callbacks.Count > 0) newCallbackCache[kvp.Key] = callbacks;
                itemsProcessed++;
            }

            CleanupExpiredUninitialized(MAX_UNINIT_RETRY_SECONDS);

            lock (_cacheUpdateLock)
            {
                _subscriptionCache = newSubscriptionCache;
                _callbackCache = newCallbackCache;
            }

            return (newSubscriptionCache, newCallbackCache);
        }

        public void UpdateL2Subscriptions(ConcurrentDictionary<string, L2Subscription> subscriptions)
        {
            var newL2Cache = new ConcurrentDictionary<string, L2Subscription>();
            foreach (var kvp in subscriptions) newL2Cache[kvp.Key] = kvp.Value;

            lock (_cacheUpdateLock)
            {
                _l2SubscriptionCache = newL2Cache;
            }
        }

        private void CleanupExpiredUninitialized(int retrySeconds)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _uninitializedSymbols
                .Where(kvp => (now - kvp.Value).TotalSeconds > retrySeconds)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _uninitializedSymbols.TryRemove(key, out _);
                Logger.Warn($"[TSR] Gave up waiting for instrument initialization: {key}");
            }
        }

        public bool TryGetCallbacks(string symbol, out List<SubscriptionCallback> callbacks) => _callbackCache.TryGetValue(symbol, out callbacks);
        public bool TryGetL2Subscription(string symbol, out L2Subscription sub) => _l2SubscriptionCache.TryGetValue(symbol, out sub);
    }
}
