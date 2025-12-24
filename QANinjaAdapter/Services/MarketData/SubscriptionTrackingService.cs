using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QANinjaAdapter.Services.MarketData
{
    /// <summary>
    /// Tracks subscriptions with reference counting to prevent premature unsubscription.
    /// This service ensures WebSocket subscriptions remain active as long as ANY consumer needs them.
    /// </summary>
    public class SubscriptionTrackingService
    {
        private static SubscriptionTrackingService _instance;
        private static readonly object _instanceLock = new object();

        // Track subscription reference counts
        private readonly ConcurrentDictionary<string, SubscriptionInfo> _subscriptions = new ConcurrentDictionary<string, SubscriptionInfo>();

        // Lock for thread-safe operations
        private readonly object _operationLock = new object();

        public static SubscriptionTrackingService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new SubscriptionTrackingService();
                        }
                    }
                }
                return _instance;
            }
        }

        private SubscriptionTrackingService()
        {
            Logger.Info("[SubscriptionTracking] Service initialized");
        }

        /// <summary>
        /// Adds a reference to a subscription. Returns true if this is the first reference (new subscription).
        /// </summary>
        /// <param name="symbol">The symbol to subscribe to</param>
        /// <param name="consumerId">Unique ID of the consumer (chart, option chain, etc.)</param>
        /// <param name="instrumentToken">The instrument token for WebSocket subscription</param>
        /// <param name="isIndex">Whether this is an index symbol</param>
        /// <param name="isSticky">If true, subscription will never be unsubscribed even if refcount=0</param>
        /// <returns>True if this is the first reference and WebSocket subscription should be created</returns>
        public bool AddReference(string symbol, string consumerId, int instrumentToken = 0, bool isIndex = false, bool isSticky = false)
        {
            lock (_operationLock)
            {
                if (_subscriptions.TryGetValue(symbol, out var info))
                {
                    // Existing subscription - increment reference count
                    if (!info.Consumers.Contains(consumerId))
                    {
                        info.Consumers.Add(consumerId);
                        info.ReferenceCount++;
                        Logger.Debug($"[SubscriptionTracking] AddReference: {symbol} - consumer={consumerId}, refCount={info.ReferenceCount}");
                    }
                    // If this reference is sticky, make the entire subscription sticky
                    if (isSticky && !info.IsSticky)
                    {
                        info.IsSticky = true;
                        Logger.Info($"[SubscriptionTracking] AddReference: {symbol} marked as STICKY");
                    }
                    return false; // Not a new subscription
                }
                else
                {
                    // New subscription
                    var newInfo = new SubscriptionInfo
                    {
                        Symbol = symbol,
                        InstrumentToken = instrumentToken,
                        IsIndex = isIndex,
                        ReferenceCount = 1,
                        SubscribedAt = DateTime.UtcNow,
                        IsSticky = isSticky
                    };
                    newInfo.Consumers.Add(consumerId);
                    _subscriptions[symbol] = newInfo;
                    Logger.Info($"[SubscriptionTracking] AddReference: NEW {symbol} - consumer={consumerId}, token={instrumentToken}, isIndex={isIndex}, sticky={isSticky}");
                    return true; // New subscription - WebSocket subscribe needed
                }
            }
        }

        public (int Count, bool IsSticky, List<string> Consumers) GetReferenceDetails(string symbol)
        {
            if (_subscriptions.TryGetValue(symbol, out var info))
            {
                // Return a copy of the consumers list to avoid thread safety issues
                return (info.ReferenceCount, info.IsSticky, info.Consumers.ToList());
            }
            return (0, false, new List<string>());
        }

        /// <summary>
        /// Removes a reference from a subscription. Returns true if this was the last reference (should unsubscribe).
        /// NOTE: Sticky subscriptions will NEVER return true - they stay active for the entire session.
        /// </summary>
        /// <param name="symbol">The symbol to potentially unsubscribe from</param>
        /// <param name="consumerId">Unique ID of the consumer releasing the subscription</param>
        /// <returns>True if this was the last reference and WebSocket should be unsubscribed (never true for sticky)</returns>
        public bool RemoveReference(string symbol, string consumerId)
        {
            lock (_operationLock)
            {
                if (_subscriptions.TryGetValue(symbol, out var info))
                {
                    if (info.Consumers.Contains(consumerId))
                    {
                        info.Consumers.Remove(consumerId);
                        info.ReferenceCount--;
                        Logger.Debug($"[SubscriptionTracking] RemoveReference: {symbol} - consumer={consumerId}, refCount={info.ReferenceCount}, sticky={info.IsSticky}");

                        if (info.ReferenceCount <= 0)
                        {
                            // STICKY SUBSCRIPTION: Never unsubscribe, keep it active
                            if (info.IsSticky)
                            {
                                Logger.Info($"[SubscriptionTracking] RemoveReference: {symbol} - refCount=0 but STICKY, keeping subscription active");
                                return false; // Don't unsubscribe - sticky subscription
                            }

                            _subscriptions.TryRemove(symbol, out _);
                            Logger.Info($"[SubscriptionTracking] RemoveReference: REMOVED {symbol} - no more references");
                            return true; // Last reference - WebSocket unsubscribe needed
                        }
                    }
                    return false; // Still has references
                }
                return false; // Symbol not found
            }
        }

        /// <summary>
        /// Checks if a symbol has any active subscriptions.
        /// </summary>
        public bool HasSubscription(string symbol)
        {
            return _subscriptions.ContainsKey(symbol);
        }

        /// <summary>
        /// Gets the reference count for a symbol.
        /// </summary>
        public int GetReferenceCount(string symbol)
        {
            if (_subscriptions.TryGetValue(symbol, out var info))
            {
                return info.ReferenceCount;
            }
            return 0;
        }

        /// <summary>
        /// Gets subscription info for a symbol.
        /// </summary>
        public SubscriptionInfo GetSubscriptionInfo(string symbol)
        {
            _subscriptions.TryGetValue(symbol, out var info);
            return info;
        }

        /// <summary>
        /// Gets the total number of active subscriptions.
        /// </summary>
        public int TotalSubscriptions => _subscriptions.Count;

        /// <summary>
        /// Gets all active subscription symbols.
        /// </summary>
        public string[] GetAllSubscribedSymbols()
        {
            return _subscriptions.Keys.ToArray();
        }

        /// <summary>
        /// Forcefully clears all subscriptions (use with caution - for shutdown only).
        /// </summary>
        public void ClearAll()
        {
            lock (_operationLock)
            {
                _subscriptions.Clear();
                Logger.Info("[SubscriptionTracking] All subscriptions cleared");
            }
        }

        /// <summary>
        /// Logs current subscription statistics.
        /// </summary>
        public void LogStats()
        {
            int total = _subscriptions.Count;
            int indexCount = 0;
            int optionCount = 0;

            foreach (var kvp in _subscriptions)
            {
                if (kvp.Value.IsIndex)
                    indexCount++;
                else
                    optionCount++;
            }

            Logger.Info($"[SubscriptionTracking] Stats: Total={total}, Indices={indexCount}, Options={optionCount}");
        }
    }

    /// <summary>
    /// Information about a tracked subscription.
    /// </summary>
    public class SubscriptionInfo
    {
        public string Symbol { get; set; }
        public int InstrumentToken { get; set; }
        public bool IsIndex { get; set; }
        public int ReferenceCount { get; set; }
        public DateTime SubscribedAt { get; set; }
        public HashSet<string> Consumers { get; } = new HashSet<string>();

        /// <summary>
        /// If true, this subscription is "sticky" and will never be unsubscribed
        /// even if reference count reaches zero. Used for Option Chain and Market Analyzer
        /// subscriptions that should stay active for the entire session.
        /// </summary>
        public bool IsSticky { get; set; }
    }
}
