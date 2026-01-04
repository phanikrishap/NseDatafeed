using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Helpers;
using System.Buffers;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// High-performance tick processor that eliminates bottlenecks causing lag.
    /// Uses BlockingCollection for reliable producer-consumer synchronization.
    ///
    /// Features:
    /// - Centralized asynchronous pipeline for all tick processing
    /// - Dedicated processing thread per shard using BlockingCollection
    /// - Intelligent backpressure management with tiered limits
    /// - O(1) callback caching for fast lookups
    /// - Memory pressure detection and automatic cache trimming
    /// </summary>
    public class OptimizedTickProcessor : IDisposable
    {
        // Sharded Configuration
        private const int SHARD_COUNT = 4; // Configurable based on CPU cores
        private const int QUEUE_CAPACITY = 16384; // Bounded capacity per shard

        /// <summary>
        /// Per-symbol state owned exclusively by a shard worker.
        /// No locks needed - single-writer guarantee.
        /// </summary>
        private class SymbolState
        {
            public int PreviousVolume;
            public double PreviousPrice;
            public DateTime LastTickTime;
        }

        private class Shard
        {
            /// <summary>
            /// BlockingCollection provides reliable producer-consumer synchronization.
            /// Handles all memory barriers and thread coordination internally.
            /// </summary>
            public readonly BlockingCollection<TickProcessingItem> Queue;
            public Task WorkerTask;

            /// <summary>
            /// Shard-local symbol state - only accessed by this shard's worker thread.
            /// Key: symbol name, Value: state (previous volume, price, etc.)
            /// No locks needed - single-writer guarantee by design.
            /// </summary>
            public readonly Dictionary<string, SymbolState> SymbolStates = new Dictionary<string, SymbolState>();

            public Shard(int capacity)
            {
                Queue = new BlockingCollection<TickProcessingItem>(capacity);
            }

            /// <summary>
            /// Get or create symbol state (called only by shard worker - no locks needed)
            /// </summary>
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

        private readonly Shard[] _shards;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly int _maxQueueSize;
        
        // Performance optimization: Cached TimeZone
        private static readonly TimeZoneInfo IstTimeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);

        // Backpressure Management - Delegated to BackpressureManager
        private readonly BackpressureManager _backpressureManager;
        private readonly int _warningQueueSize;
        private readonly int _criticalQueueSize;
        private readonly int _maxAcceptableQueueSize;

        // Health Monitoring - Delegated to ProcessorHealthMonitor
        private readonly ProcessorHealthMonitor _healthMonitor;

        // High-performance caches for O(1) lookups
        // IMPORTANT: These are volatile references for atomic swap during updates
        // This prevents race conditions where readers see partially cleared caches
        private readonly ConcurrentDictionary<string, string> _symbolMappingCache = new ConcurrentDictionary<string, string>();
        private volatile ConcurrentDictionary<string, L1Subscription> _subscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
        private volatile ConcurrentDictionary<string, L2Subscription> _l2SubscriptionCache = new ConcurrentDictionary<string, L2Subscription>();
        private volatile ConcurrentDictionary<string, List<SubscriptionCallback>> _callbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();
        private readonly HashSet<string> _loggedMissedSymbols = new HashSet<string>(); // Track which symbols we've logged misses for
        private readonly object _loggedMissedSymbolsLock = new object();
        private const int MaxLoggedMissedSymbols = 500; // Limit to prevent unbounded growth

        // TICK CACHE: Store last tick for each instrument to replay when callbacks are registered late
        // This is critical for post/pre-market when Zerodha sends only ONE snapshot tick per instrument
        private readonly ConcurrentDictionary<string, ZerodhaTickData> _lastTickCache = new ConcurrentDictionary<string, ZerodhaTickData>();
        private const int MaxTickCacheSize = 1000; // Limit cache size
        private long _tickCacheHits = 0; // Track cache replay statistics
        private long _tickCacheMisses = 0;
        // Track which specific callbacks have received their initial tick (keyed by symbol + callback hashcode)
        // This allows NEW callbacks to receive cached ticks even if other callbacks for the same symbol already did
        private readonly ConcurrentDictionary<string, bool> _initializedCallbacks = new ConcurrentDictionary<string, bool>();

        // Performance monitoring
        private readonly PerformanceMonitor _performanceMonitor;

        // Performance counters
        private long _ticksProcessed = 0;
        private long _ticksQueued = 0;

        // Callback performance tracking
        private long _callbacksExecuted = 0;
        private long _callbackErrors = 0;
        private long _totalCallbackTimeMs = 0;
        private long _slowCallbacks = 0; // Callbacks taking >1ms
        private long _verySlowCallbacks = 0; // Callbacks taking >5ms
        private long _optionTickLogCounter = 0; // For diagnostic logging
        private long _optionCallbackFiredCounter = 0; // Track when option callbacks actually fire
        private long _noCallbackLogCounter = 0; // Track subscription exists but no callback cases

        /// <summary>
        /// Event-driven tick notification for option prices.
        /// Fires for every option tick with (symbol, ltp) - allows SubscriptionManager to update UI directly.
        /// This is the reliable, event-driven path that bypasses callback chain issues.
        /// </summary>
        public event Action<string, double> OptionTickReceived;

        // ═══════════════════════════════════════════════════════════════════
        // REACTIVE EXTENSIONS (Rx.NET) - Phase 2 Event-Driven Architecture
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Core reactive tick stream - publishes ALL processed ticks.
        /// Use this for components that need real-time tick data with Rx operators.
        /// Subject is thread-safe and supports multiple subscribers.
        /// </summary>
        private readonly Subject<TickStreamItem> _tickSubject = new Subject<TickStreamItem>();

        /// <summary>
        /// Exposes the tick stream as an IObservable for subscribers.
        /// Consumers can apply Rx operators: .Where(), .Throttle(), .Buffer(), etc.
        /// </summary>
        public IObservable<TickStreamItem> TickStream => _tickSubject.AsObservable();

        /// <summary>
        /// Option-only tick stream with automatic filtering.
        /// Pre-filtered for CE/PE symbols - no need to filter on consumer side.
        /// </summary>
        public IObservable<TickStreamItem> OptionTickStream =>
            _tickSubject.Where(t => t.IsOption).AsObservable();

        /// <summary>
        /// Index-only tick stream (NIFTY, BANKNIFTY, SENSEX).
        /// Pre-filtered for index symbols.
        /// </summary>
        public IObservable<TickStreamItem> IndexTickStream =>
            _tickSubject.Where(t => t.IsIndex).AsObservable();

        /// <summary>
        /// Throttled option tick stream for UI updates (max 10 updates/second per symbol).
        /// Prevents UI from being overwhelmed during high-frequency trading.
        /// Uses GroupBy + Sample to throttle per-symbol independently.
        /// </summary>
        public IObservable<TickStreamItem> ThrottledOptionTickStream =>
            _tickSubject
                .Where(t => t.IsOption)
                .GroupBy(t => t.Symbol)
                .SelectMany(grp => grp.Sample(TimeSpan.FromMilliseconds(100))) // 10 updates/sec per symbol
                .AsObservable();

        /// <summary>
        /// Batched tick stream for bulk operations (groups ticks every 100ms).
        /// Useful for batch database writes or network transmissions.
        /// </summary>
        public IObservable<IList<TickStreamItem>> BatchedTickStream =>
            _tickSubject
                .Buffer(TimeSpan.FromMilliseconds(100))
                .Where(batch => batch.Count > 0)
                .AsObservable();

        // Note: Backpressure tracking moved to BackpressureManager
        // Note: Health monitoring moved to ProcessorHealthMonitor

        // Memory pressure optimization
        private int _lastGCGeneration2Count = 0;
        private DateTime _lastMemoryPressureCheck = DateTime.UtcNow;
        private volatile bool _isUnderMemoryPressure = false;

        private readonly object _cacheUpdateLock = new object();
        private volatile bool _isDisposed = false;

        public OptimizedTickProcessor(int maxQueueSize = 20000)
        {
            _maxQueueSize = maxQueueSize;

            // Initialize tiered backpressure limits
            _warningQueueSize = (int)(maxQueueSize * 0.6);
            _criticalQueueSize = (int)(maxQueueSize * 0.8);
            _maxAcceptableQueueSize = (int)(maxQueueSize * 0.9);

            _cancellationTokenSource = new CancellationTokenSource();
            _performanceMonitor = new PerformanceMonitor(TimeSpan.FromSeconds(30));

            // Initialize BackpressureManager (delegated)
            _backpressureManager = new BackpressureManager(maxQueueSize);

            // Initialize Shards with BlockingCollection
            _shards = new Shard[SHARD_COUNT];
            for (int i = 0; i < SHARD_COUNT; i++)
            {
                int shardIndex = i;
                _shards[i] = new Shard(QUEUE_CAPACITY);
                _shards[i].WorkerTask = Task.Factory.StartNew(
                    () => ProcessShardSynchronously(shardIndex),
                    _cancellationTokenSource.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                );
            }

            // Initialize health monitoring (delegated to ProcessorHealthMonitor)
            _healthMonitor = new ProcessorHealthMonitor(
                _warningQueueSize,
                _criticalQueueSize,
                GetHealthMetricsSnapshot,
                monitoringIntervalMs: 30000
            );
            _healthMonitor.HealthAlertRaised += OnHealthAlert;
            _healthMonitor.Start();

            Logger.Info($"[OTP] OptimizedTickProcessor INITIALIZED with {SHARD_COUNT} shards, BlockingCollection capacity={QUEUE_CAPACITY}");
            Log($"OptimizedTickProcessor: Initialized with {SHARD_COUNT} shards and BlockingCollection queues.");
        }

        /// <summary>
        /// Provides metrics snapshot for health monitoring
        /// </summary>
        private HealthMetricsSnapshot GetHealthMetricsSnapshot()
        {
            var bpMetrics = _backpressureManager.GetMetrics();
            return new HealthMetricsSnapshot
            {
                TicksQueued = Interlocked.Read(ref _ticksQueued),
                TicksProcessed = Interlocked.Read(ref _ticksProcessed),
                CallbacksExecuted = Interlocked.Read(ref _callbacksExecuted),
                CallbackErrors = Interlocked.Read(ref _callbackErrors),
                TotalCallbackTimeMs = Interlocked.Read(ref _totalCallbackTimeMs),
                SlowCallbacks = Interlocked.Read(ref _slowCallbacks),
                VerySlowCallbacks = Interlocked.Read(ref _verySlowCallbacks),
                TicksDroppedBackpressure = bpMetrics.TicksDropped,
                BackpressureState = bpMetrics.CurrentState
            };
        }

        /// <summary>
        /// Handle health alerts from the monitor
        /// </summary>
        private void OnHealthAlert(object sender, HealthAlertEventArgs e)
        {
            Log($"ALERT: {e.Message}");
        }

        /// <summary>
        /// Process ticks for a specific shard.
        /// Uses BlockingCollection.GetConsumingEnumerable for reliable consumption.
        /// </summary>
        private void ProcessShardSynchronously(int shardIndex)
        {
            var shard = _shards[shardIndex];
            Logger.Debug($"[OTP-SHARD] Shard {shardIndex} worker STARTING (BlockingCollection)");
            long processedCount = 0;

            try
            {
                // GetConsumingEnumerable blocks until items are available
                // and handles all synchronization internally - no manual memory barriers needed
                foreach (var item in shard.Queue.GetConsumingEnumerable(_cancellationTokenSource.Token))
                {
                    processedCount++;

                    // DIAGNOSTIC: Log progress (every 100 ticks or first tick)
                    if (processedCount % 100 == 1 || processedCount == 1)
                    {
                        Logger.Debug($"[OTP-SHARD] Shard {shardIndex}: processed={processedCount}, queueCount={shard.Queue.Count}");
                    }

                    if (item?.TickData != null)
                    {
                        try
                        {
                            ProcessSingleTick(item, shard);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[OTP-SHARD] Shard {shardIndex}: Error processing tick: {ex.Message}", ex);
                        }
                    }

                    Interlocked.Increment(ref _ticksProcessed);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown - cancellation token was triggered
                Logger.Debug($"[OTP-SHARD] Shard {shardIndex} worker cancelled (normal shutdown)");
            }
            catch (Exception ex)
            {
                Logger.Error($"[OTP-SHARD] CRITICAL: Shard {shardIndex} failed: {ex.Message}", ex);
            }
            finally
            {
                Logger.Debug($"[OTP-SHARD] Shard {shardIndex} worker ended, processed={processedCount} ticks");
            }
        }

        /// <summary>
        /// Queues a tick for processing (non-blocking).
        /// Uses BlockingCollection.TryAdd for reliable producer-consumer synchronization.
        /// </summary>
        public bool QueueTick(string nativeSymbolName, ZerodhaTickData tickData)
        {
            if (_isDisposed) return false;

            // Record tick received
            _performanceMonitor.RecordTickReceived(nativeSymbolName);

            // Shard selection based on symbol hash
            int shardIndex = (nativeSymbolName.GetHashCode() & 0x7FFFFFFF) % SHARD_COUNT;
            var shard = _shards[shardIndex];

            // Backpressure check using queue count
            int currentCount = shard.Queue.Count;
            if (currentCount >= QUEUE_CAPACITY - 2048) // Safety margin before full
            {
                var backpressureResult = ApplyBackpressureManagement(currentCount, null, nativeSymbolName);
                if (!backpressureResult.shouldQueue)
                {
                    _performanceMonitor.RecordTickDropped(nativeSymbolName);
                    return false;
                }
            }

            // Create item for the queue
            var item = new TickProcessingItem
            {
                NativeSymbolName = nativeSymbolName,
                TickData = tickData,
                QueueTime = DateTime.UtcNow
            };

            // DIAGNOSTIC: Log first few queued ticks per shard
            long queuedCount = Interlocked.Read(ref _ticksQueued);
            if (queuedCount < 20)
            {
                Logger.Info($"[OTP-QUEUE] Shard {shardIndex}: Queuing tick #{queuedCount}, symbol={nativeSymbolName}, queueCount={shard.Queue.Count}");
            }

            // TryAdd is non-blocking - returns false if queue is full
            if (shard.Queue.TryAdd(item))
            {
                Interlocked.Increment(ref _ticksQueued);
                return true;
            }
            else
            {
                // Queue is full - apply backpressure
                _performanceMonitor.RecordTickDropped(nativeSymbolName);
                _backpressureManager.RecordTickDropped();
                return false;
            }
        }

        /// <summary>
        /// Intelligent backpressure management - delegated to BackpressureManager
        /// </summary>
        private (bool shouldQueue, string reason) ApplyBackpressureManagement(long currentQueueDepth, TickProcessingItem item, string symbol)
        {
            return _backpressureManager.EvaluateTick(currentQueueDepth, symbol);
        }


        // Track symbols with uninitialized instruments for retry
        private readonly ConcurrentDictionary<string, DateTime> _uninitializedSymbols = new ConcurrentDictionary<string, DateTime>();
        private const int MAX_UNINIT_RETRY_SECONDS = 30;

        /// <summary>
        /// Updates subscription cache (thread-safe) using atomic swap pattern.
        /// This prevents race conditions where readers see partially cleared caches.
        /// </summary>
        public void UpdateSubscriptionCache(ConcurrentDictionary<string, L1Subscription> subscriptions)
        {
            // Check memory pressure before expensive cache update
            CheckMemoryPressure();

            // Log every cache update at INFO level for debugging
            Logger.Info($"[OTP] UpdateSubscriptionCache CALLED with {subscriptions?.Count ?? 0} items. Current cache has {_subscriptionCache.Count} subs, {_callbackCache.Count} callbacks");

            // CRITICAL DEBUG: Log cache update
            if (subscriptions == null || subscriptions.Count == 0)
            {
                 Logger.Warn("[OTP] UpdateSubscriptionCache called with empty/null subscriptions - skipping to preserve existing cache");
                 return;
            }

            if (subscriptions.Count < 5)
            {
                 // Suspicious: updating cache with very few items?
                 Logger.Debug($"[OTP-CACHE] UpdateSubscriptionCache called with only {subscriptions.Count} items! Possible cache stomp.");
                 foreach(var key in subscriptions.Keys) Logger.Debug($"[OTP-CACHE] Key: {key}");
            }

            // Build NEW caches without touching the current ones
            // This ensures readers always see a complete, consistent view
            var newSubscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
            var newCallbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();

            // Limit cache size under memory pressure
            int maxCacheSize = _isUnderMemoryPressure ? 500 : 2000;
            int itemsProcessed = 0;
            int skippedUninitializedCount = 0;

            foreach (var kvp in subscriptions)
            {
                if (itemsProcessed >= maxCacheSize)
                {
                    Log($"MEMORY PRESSURE: Cache size limited to {maxCacheSize} items (total subscriptions: {subscriptions.Count})");
                    break;
                }

                newSubscriptionCache[kvp.Key] = kvp.Value;

                // Pre-build callback list for O(1) retrieval during tick processing
                // Only add callbacks for fully initialized instruments
                var callbacks = new List<SubscriptionCallback>();

                // Use thread-safe snapshot from L1Subscription (no locking needed)
                // This prevents "Collection was modified" exceptions
                var callbackSnapshot = kvp.Value.GetCallbacksSnapshot();
                foreach (var callbackPair in callbackSnapshot)
                {
                    var instrument = callbackPair.Key;

                    // Check if NinjaTrader instrument is fully initialized
                    if (instrument == null || instrument.MasterInstrument == null)
                    {
                        skippedUninitializedCount++;

                        // Track for retry later
                        if (!_uninitializedSymbols.ContainsKey(kvp.Key))
                        {
                            _uninitializedSymbols[kvp.Key] = DateTime.UtcNow;
                            Logger.Warn($"[OTP] Skipping uninitialized instrument for {kvp.Key} - will retry later");
                        }
                        continue;
                    }

                    // Instrument is ready, add to callbacks
                    callbacks.Add(new SubscriptionCallback
                    {
                        Instrument = instrument,
                        Callback = callbackPair.Value
                    });

                    // Remove from uninitialized tracking if it was there
                    _uninitializedSymbols.TryRemove(kvp.Key, out _);
                }

                if (callbacks.Count > 0)
                {
                    newCallbackCache[kvp.Key] = callbacks;
                }
                itemsProcessed++;
            }

            // Clean up old uninitialized entries (prevent memory leak)
            var now = DateTime.UtcNow;
            var expiredKeys = _uninitializedSymbols
                .Where(kvp => (now - kvp.Value).TotalSeconds > MAX_UNINIT_RETRY_SECONDS)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in expiredKeys)
            {
                _uninitializedSymbols.TryRemove(key, out _);
                Logger.Warn($"[OTP] Gave up waiting for instrument initialization: {key}");
            }

            if (skippedUninitializedCount > 0)
            {
                Logger.Info($"[OTP] Skipped {skippedUninitializedCount} uninitialized instrument callbacks - they will be retried");
            }
            else
            {
                Logger.Info($"[OTP] All instrument callbacks are initialized - no skips");
            }

            // ATOMIC SWAP: Replace old caches with new ones in a single operation
            // Readers will either see the old complete cache or the new complete cache, never a partial one
            lock (_cacheUpdateLock)
            {
                _subscriptionCache = newSubscriptionCache;
                _callbackCache = newCallbackCache;
            }

            Log($"OptimizedTickProcessor: Updated caches - {newSubscriptionCache.Count} subscriptions (memory pressure: {_isUnderMemoryPressure})");
            Logger.Info($"[OTP] Updated caches - {newSubscriptionCache.Count} subscriptions, {newCallbackCache.Count} callback entries, {_uninitializedSymbols.Count} pending init");

            // TICK CACHE REPLAY: Replay cached ticks for newly registered callbacks
            // This is critical for post/pre-market when ticks arrive before callbacks are registered
            ReplayCachedTicksForNewCallbacks(newCallbackCache);

            // Force GC if under pressure and cache is large
            if (_isUnderMemoryPressure && newSubscriptionCache.Count > 100)
            {
                GC.Collect(0, GCCollectionMode.Optimized);
            }
        }

        /// <summary>
        /// Updates L2 subscription cache (thread-safe) using atomic swap pattern.
        /// </summary>
        public void UpdateL2SubscriptionCache(ConcurrentDictionary<string, L2Subscription> subscriptions)
        {
            // Build new cache without touching current one
            var newL2Cache = new ConcurrentDictionary<string, L2Subscription>();
            foreach (var kvp in subscriptions)
            {
                newL2Cache[kvp.Key] = kvp.Value;
            }

            // Atomic swap
            lock (_cacheUpdateLock)
            {
                _l2SubscriptionCache = newL2Cache;
            }

            Log($"OptimizedTickProcessor: Updated L2 caches - {newL2Cache.Count} subscriptions");
        }

        #region Tick Cache for Post/Pre-Market Replay

        /// <summary>
        /// Caches the last tick for a symbol. Used to replay when callbacks are registered late.
        /// This is critical for post/pre-market when Zerodha sends only ONE snapshot tick per instrument.
        /// </summary>
        private void CacheLastTick(string symbol, ZerodhaTickData tickData)
        {
            if (string.IsNullOrEmpty(symbol) || tickData == null || tickData.LastTradePrice <= 0)
                return;

            // CRITICAL DEBUG: Log EVERY option tick entering CacheLastTick (first 20)
            if ((symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX"))
            {
                long count = Interlocked.Increment(ref _cacheLastTickLogCounter);
                if (count <= 20)
                {
                    Logger.Info($"[OTP-CACHE-ENTRY] #{count} CacheLastTick for '{symbol}': LTP={tickData.LastTradePrice}, callbackCache has {_callbackCache.Count} entries, hasCallback={_callbackCache.ContainsKey(symbol)}");
                }
            }

            // Create a copy of the tick data to cache (avoid holding reference to pooled object)
            var cachedTick = new ZerodhaTickData
            {
                InstrumentIdentifier = tickData.InstrumentIdentifier,
                InstrumentToken = tickData.InstrumentToken,
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

            // Copy BidDepth and AskDepth (BuyPrice/SellPrice are computed from these)
            // NOTE: The new ZerodhaTickData has arrays of null DepthEntry references,
            // so we need to create DepthEntry objects before setting their properties
            if (tickData.BidDepth != null && tickData.BidDepth.Length > 0 && tickData.BidDepth[0] != null)
            {
                cachedTick.BidDepth[0] = new Models.DepthEntry
                {
                    Price = tickData.BidDepth[0].Price,
                    Quantity = tickData.BidDepth[0].Quantity
                };
            }
            if (tickData.AskDepth != null && tickData.AskDepth.Length > 0 && tickData.AskDepth[0] != null)
            {
                cachedTick.AskDepth[0] = new Models.DepthEntry
                {
                    Price = tickData.AskDepth[0].Price,
                    Quantity = tickData.AskDepth[0].Quantity
                };
            }

            _lastTickCache[symbol] = cachedTick;

            // ═══════════════════════════════════════════════════════════════════
            // REACTIVE STREAM: Publish to Rx Subject for all subscribers
            // This is the primary event-driven path - all consumers should use this
            // ═══════════════════════════════════════════════════════════════════
            try
            {
                DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, IstTimeZone);
                var streamItem = new TickStreamItem(symbol, tickData, now);
                _tickSubject.OnNext(streamItem);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[OTP-RX] Error publishing to reactive stream for {symbol}: {ex.Message}");
            }

            // Log option tick caching for debugging post-market LTP issues
            bool isOption = (symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX");
            if (isOption)
            {
                if (_lastTickCache.Count <= 10 || _lastTickCache.Count % 50 == 0)
                {
                    Logger.Info($"[OTP-CACHE] Cached tick for {symbol}: LTP={tickData.LastTradePrice}, CacheSize={_lastTickCache.Count}");
                }

                // EVENT-DRIVEN (Legacy): Fire OptionTickReceived for backward compatibility
                // New consumers should use OptionTickStream instead
                try
                {
                    OptionTickReceived?.Invoke(symbol, tickData.LastTradePrice);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[OTP-EVENT] Error firing OptionTickReceived for {symbol}: {ex.Message}");
                }
            }

            // IMMEDIATE REPLAY: If callback exists but hasn't been replayed yet, replay now
            // This handles the case where ticks arrive AFTER callbacks are registered
            TryImmediateReplay(symbol, cachedTick);

            // Prevent unbounded growth - trim if needed
            if (_lastTickCache.Count > MaxTickCacheSize)
            {
                // Remove oldest entries (simple approach: just remove some random ones)
                int toRemove = _lastTickCache.Count - MaxTickCacheSize + 100; // Remove 100 extra to avoid frequent trims
                var keysToRemove = _lastTickCache.Keys.Take(toRemove).ToList();
                foreach (var key in keysToRemove)
                {
                    _lastTickCache.TryRemove(key, out _);
                }
                Logger.Debug($"[OTP-CACHE] Trimmed tick cache from {_lastTickCache.Count + toRemove} to {_lastTickCache.Count} entries");
            }
        }

        /// <summary>
        /// Tries to immediately replay a cached tick to registered callbacks.
        /// Called when a tick is cached and we want to check if callbacks exist for it.
        /// This handles the case where ticks arrive AFTER callbacks are registered.
        /// Uses per-callback tracking so NEW callbacks for the same symbol still get the cached tick.
        /// </summary>
        private void TryImmediateReplay(string symbol, ZerodhaTickData cachedTick)
        {
            if (string.IsNullOrEmpty(symbol) || cachedTick == null || cachedTick.LastTradePrice <= 0)
                return;

            // Check if callbacks exist for this symbol
            if (!_callbackCache.TryGetValue(symbol, out var callbacks) || callbacks == null || callbacks.Count == 0)
                return; // No callbacks yet

            DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, IstTimeZone);
            bool isOption = (symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX");
            int firedCount = 0;

            foreach (var callbackInfo in callbacks)
            {
                try
                {
                    if (callbackInfo?.Callback == null || callbackInfo.Instrument?.MasterInstrument == null)
                        continue;

                    // Create a unique key for this specific callback (symbol + callback hashcode)
                    string callbackKey = $"{symbol}_{callbackInfo.Callback.GetHashCode()}";

                    // ATOMIC check-and-set: TryAdd returns false if key already exists
                    // This prevents race condition where two threads both see "not exists" and both fire
                    if (!_initializedCallbacks.TryAdd(callbackKey, true))
                        continue; // Another thread already initialized this callback

                    var callback = callbackInfo.Callback;

                    // Fire LastPrice callback
                    callback(MarketDataType.Last, cachedTick.LastTradePrice, Math.Max(1, cachedTick.LastTradeQty), now, 0L);

                    // Also fire bid/ask if available
                    if (cachedTick.BuyPrice > 0)
                        callback(MarketDataType.Bid, cachedTick.BuyPrice, cachedTick.BuyQty, now, 0L);
                    if (cachedTick.SellPrice > 0)
                        callback(MarketDataType.Ask, cachedTick.SellPrice, cachedTick.SellQty, now, 0L);

                    // Fire daily stats if available
                    if (cachedTick.TotalQtyTraded > 0)
                        callback(MarketDataType.DailyVolume, cachedTick.TotalQtyTraded, cachedTick.TotalQtyTraded, now, 0L);
                    if (cachedTick.High > 0)
                        callback(MarketDataType.DailyHigh, cachedTick.High, 0L, now, 0L);
                    if (cachedTick.Low > 0)
                        callback(MarketDataType.DailyLow, cachedTick.Low, 0L, now, 0L);
                    if (cachedTick.OpenInterest > 0)
                        callback(MarketDataType.OpenInterest, cachedTick.OpenInterest, cachedTick.OpenInterest, now, 0L);

                    firedCount++;
                    Interlocked.Increment(ref _tickCacheHits);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[OTP-IMMED-REPLAY] Error replaying to callback for {symbol}: {ex.Message}");
                }
            }

            // Log replay for options (only if we actually fired)
            if (isOption && firedCount > 0)
            {
                Logger.Info($"[OTP-IMMED-REPLAY] Immediate replay for {symbol}: LTP={cachedTick.LastTradePrice}, callbacks={firedCount}");
            }
        }

        /// <summary>
        /// Replays cached ticks for newly registered callbacks.
        /// Called when subscription cache is updated with new callbacks.
        /// This ensures symbols that received ticks BEFORE their callback was registered still get data.
        /// Uses per-callback tracking so NEW callbacks get cached ticks even if other callbacks already did.
        /// </summary>
        private void ReplayCachedTicksForNewCallbacks(ConcurrentDictionary<string, List<SubscriptionCallback>> newCallbackCache)
        {
            // Log entry for debugging
            Logger.Info($"[OTP-REPLAY] ReplayCachedTicksForNewCallbacks called: callbackCache={newCallbackCache?.Count ?? 0}, tickCache={_lastTickCache.Count}");

            if (newCallbackCache == null || newCallbackCache.Count == 0 || _lastTickCache.Count == 0)
            {
                Logger.Info($"[OTP-REPLAY] Early exit: callbacks={newCallbackCache?.Count ?? 0}, tickCache={_lastTickCache.Count}");
                return;
            }

            int replayedCount = 0;
            int callbacksFired = 0;
            DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, IstTimeZone);

            foreach (var kvp in newCallbackCache)
            {
                string symbol = kvp.Key;
                var callbacks = kvp.Value;

                if (callbacks == null || callbacks.Count == 0)
                    continue;

                // Check if we have a cached tick for this symbol
                if (!_lastTickCache.TryGetValue(symbol, out var cachedTick) || cachedTick.LastTradePrice <= 0)
                {
                    Interlocked.Increment(ref _tickCacheMisses);
                    continue;
                }

                bool symbolHadNewCallbacks = false;

                // Replay to each callback that hasn't received a tick yet
                foreach (var callbackInfo in callbacks)
                {
                    try
                    {
                        if (callbackInfo?.Callback == null || callbackInfo.Instrument?.MasterInstrument == null)
                            continue;

                        // Create unique key for this specific callback
                        string callbackKey = $"{symbol}_{callbackInfo.Callback.GetHashCode()}";

                        // ATOMIC check-and-set: TryAdd returns false if key already exists
                        // This prevents race condition where two threads both fire the same callback
                        if (!_initializedCallbacks.TryAdd(callbackKey, true))
                            continue; // Another thread already initialized this callback

                        symbolHadNewCallbacks = true;
                        callbacksFired++;

                        var callback = callbackInfo.Callback;

                        // Fire LastPrice callback
                        callback(MarketDataType.Last, cachedTick.LastTradePrice, Math.Max(1, cachedTick.LastTradeQty), now, 0L);

                        // Also fire bid/ask if available
                        if (cachedTick.BuyPrice > 0)
                            callback(MarketDataType.Bid, cachedTick.BuyPrice, cachedTick.BuyQty, now, 0L);
                        if (cachedTick.SellPrice > 0)
                            callback(MarketDataType.Ask, cachedTick.SellPrice, cachedTick.SellQty, now, 0L);

                        // Fire daily stats if available
                        if (cachedTick.TotalQtyTraded > 0)
                            callback(MarketDataType.DailyVolume, cachedTick.TotalQtyTraded, cachedTick.TotalQtyTraded, now, 0L);
                        if (cachedTick.High > 0)
                            callback(MarketDataType.DailyHigh, cachedTick.High, 0L, now, 0L);
                        if (cachedTick.Low > 0)
                            callback(MarketDataType.DailyLow, cachedTick.Low, 0L, now, 0L);
                        if (cachedTick.Open > 0)
                            callback(MarketDataType.Opening, cachedTick.Open, 0L, now, 0L);
                        if (cachedTick.Close > 0)
                            callback(MarketDataType.LastClose, cachedTick.Close, 0L, now, 0L);
                        if (cachedTick.OpenInterest > 0)
                            callback(MarketDataType.OpenInterest, cachedTick.OpenInterest, cachedTick.OpenInterest, now, 0L);

                        Interlocked.Increment(ref _tickCacheHits);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"[OTP-REPLAY] Error replaying cached tick for {symbol}: {ex.Message}");
                    }
                }

                if (symbolHadNewCallbacks)
                {
                    replayedCount++;

                    // Log option replays
                    if ((symbol.Contains("CE") || symbol.Contains("PE")) && !symbol.Contains("SENSEX") && !symbol.Contains("BANKNIFTY"))
                    {
                        Logger.Info($"[OTP-REPLAY] Replayed cached tick for {symbol}: LTP={cachedTick.LastTradePrice}");
                    }
                }
            }

            if (replayedCount > 0)
            {
                Logger.Info($"[OTP-REPLAY] Replayed {replayedCount} symbols ({callbacksFired} callbacks) (cache hits={_tickCacheHits}, misses={_tickCacheMisses})");
            }
        }

        /// <summary>
        /// Clears the initialized callbacks tracking (call when Option Chain is closed/reopened).
        /// </summary>
        public void ClearReplayedSymbolsTracking()
        {
            _initializedCallbacks.Clear();
            Logger.Debug("[OTP-REPLAY] Cleared initialized callbacks tracking");
        }

        /// <summary>
        /// Gets tick cache statistics for diagnostics.
        /// </summary>
        public (int cacheSize, long hits, long misses, int replayedCount) GetTickCacheStats()
        {
            return (_lastTickCache.Count, _tickCacheHits, _tickCacheMisses, _initializedCallbacks.Count);
        }

        #endregion

        /// <summary>
        /// Detect memory pressure and adjust behavior
        /// </summary>
        private void CheckMemoryPressure()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMemoryPressureCheck).TotalSeconds < 5) return; // Check every 5 seconds max

            _lastMemoryPressureCheck = now;

            // Check GC pressure
            int currentGC2Count = GC.CollectionCount(2);
            bool hasGCPressure = (currentGC2Count - _lastGCGeneration2Count) > 2; // More than 2 Gen2 GCs in 5 seconds
            _lastGCGeneration2Count = currentGC2Count;

            // Check memory usage
            long memoryBytes = GC.GetTotalMemory(false);
            long memoryMB = memoryBytes / 1024 / 1024;
            bool hasMemoryPressure = memoryMB > 400; // Over 400MB is concerning

            bool wasUnderPressure = _isUnderMemoryPressure;
            _isUnderMemoryPressure = hasGCPressure || hasMemoryPressure;

            if (_isUnderMemoryPressure && !wasUnderPressure)
            {
                Log($"MEMORY PRESSURE DETECTED: GC2={currentGC2Count}, Memory={memoryMB}MB");

                // Trigger cleanup strategies
                TrimCaches();

                // Suggest GC collection if severe pressure
                if (memoryMB > 500)
                {
                    GC.Collect(2, GCCollectionMode.Forced);
                    GC.WaitForPendingFinalizers();
                    Log($"FORCED GC COLLECTION: New memory usage: {GC.GetTotalMemory(true) / 1024 / 1024}MB");
                }
            }
            else if (!_isUnderMemoryPressure && wasUnderPressure)
            {
                Log($"MEMORY PRESSURE RELIEVED: GC2={currentGC2Count}, Memory={memoryMB}MB");
            }
        }

        /// <summary>
        /// Trim caches to reduce memory usage
        /// </summary>
        private void TrimCaches()
        {
            // Clear symbol mapping cache of less important entries
            if (_symbolMappingCache.Count > 200)
            {
                var keysToRemove = _symbolMappingCache.Keys.Skip(100).ToList();
                foreach (var key in keysToRemove)
                {
                    _symbolMappingCache.TryRemove(key, out _);
                }
                Log($"Trimmed symbol mapping cache by {keysToRemove.Count} entries");
            }
        }

        // Counter for periodic cleanup of logged missed symbols
        private int _cleanupCycleCounter = 0;
        private const int CleanupCycleThreshold = 30; // Clear every 30 cycles (15 minutes at 30s intervals)

        /// <summary>
        /// Periodically cleans up the _loggedMissedSymbols HashSet to prevent unbounded growth.
        /// Called from health monitoring timer (every 30 seconds).
        /// </summary>
        private void CleanupLoggedMissedSymbols()
        {
            _cleanupCycleCounter++;

            lock (_loggedMissedSymbolsLock)
            {
                // Clear if we've hit the cycle threshold OR approaching the max limit
                if (_cleanupCycleCounter >= CleanupCycleThreshold ||
                    _loggedMissedSymbols.Count >= MaxLoggedMissedSymbols - 50)
                {
                    int clearedCount = _loggedMissedSymbols.Count;
                    _loggedMissedSymbols.Clear();
                    _cleanupCycleCounter = 0;

                    if (clearedCount > 0)
                    {
                        Logger.Info($"[OTP] Cleared {clearedCount} entries from _loggedMissedSymbols cache (periodic cleanup)");
                    }
                }
            }
        }

        /// <summary>
        /// Updates symbol mapping cache for fast lookups
        /// </summary>
        public void UpdateSymbolMappingCache(Dictionary<string, string> symbolMappings)
        {
            _symbolMappingCache.Clear();

            foreach (var kvp in symbolMappings)
            {
                _symbolMappingCache[kvp.Value] = kvp.Key; // native -> NT mapping
            }
        }


        /// <summary>
        /// Process a single tick with optimized lookups and performance tracking.
        /// Uses shard-local state for thread-safe volume delta calculations.
        /// </summary>
        private long _optionProcessSingleTickCounter = 0; // Diagnostic counter
        private long _cacheLastTickLogCounter = 0; // Diagnostic counter for CacheLastTick logging

        private void ProcessSingleTick(TickProcessingItem item, Shard shard)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Essential validation only
                if (item?.TickData == null || string.IsNullOrEmpty(item.NativeSymbolName))
                {
                    return;
                }

                string ntSymbolName = item.NativeSymbolName;

                // DIAGNOSTIC: Log option ticks entering ProcessSingleTick (every 50th)
                if ((ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")) &&
                    !ntSymbolName.Contains("SENSEX") && !ntSymbolName.Contains("BANKNIFTY"))
                {
                    if (Interlocked.Increment(ref _optionProcessSingleTickCounter) % 50 == 1)
                    {
                        Logger.Debug($"[OTP-ENTRY] ProcessSingleTick: {ntSymbolName}, LTP={item.TickData.LastTradePrice}, Vol={item.TickData.TotalQtyTraded}");
                    }
                }

                // Fast symbol lookup using cache (O(1))
                if (_symbolMappingCache.TryGetValue(item.NativeSymbolName, out string mappedName))
                {
                    ntSymbolName = mappedName;
                }

                if (string.IsNullOrEmpty(ntSymbolName))
                {
                    return;
                }

                // TICK CACHE: Always cache the last tick for each symbol
                // This is critical for post/pre-market when Zerodha sends only ONE snapshot tick
                // The tick will be replayed when a callback is registered later
                if (item.TickData.LastTradePrice > 0)
                {
                    CacheLastTick(ntSymbolName, item.TickData);
                }

                // Fast subscription lookup using cache (O(1))
                if (!_subscriptionCache.TryGetValue(ntSymbolName, out var subscription))
                {
                    // Log first miss for each symbol to help debug - include NIFTY options
                    bool shouldLog = false;
                    lock (_loggedMissedSymbolsLock)
                    {
                        if (_subscriptionCache.Count > 0 &&
                            !_loggedMissedSymbols.Contains(ntSymbolName) &&
                            _loggedMissedSymbols.Count < MaxLoggedMissedSymbols)
                        {
                            // Log NIFTY/SENSEX option misses
                            if ((ntSymbolName.Contains("NIFTY") || ntSymbolName.Contains("SENSEX")) &&
                                (ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")))
                            {
                                _loggedMissedSymbols.Add(ntSymbolName);
                                shouldLog = true;
                            }
                        }
                    }

                    if (shouldLog)
                    {
                        Logger.Info($"[OTP-MISS] No subscription for option '{ntSymbolName}' - tick CACHED for later replay. SubscriptionCache has {_subscriptionCache.Count} entries.");
                    }

                    return; // No subscription found - but tick is cached for later replay
                }

                // CRITICAL DEBUG: Log ALL SENSEX option ticks to verify data flow
                if (ntSymbolName.Contains("SENSEX") && ntSymbolName.Contains("CE") && item.TickData.LastTradePrice > 0)
                {
                    // Rate limit: only log every 10th tick or if price > 0
                     if (DateTime.Now.Millisecond < 50) // roughly 5% sample
                        Logger.Info($"[OTP-DEBUG] Processing tick for {ntSymbolName}: LTP={item.TickData.LastTradePrice}, Vol={item.TickData.TotalQtyTraded}");
                }

                // Fast callback lookup using pre-built cache (O(1))
                if (!_callbackCache.TryGetValue(ntSymbolName, out var callbacks) || callbacks?.Count == 0)
                {
                    // DIAGNOSTIC: Log when subscription exists but no callbacks (first 20, then every 100th)
                    if ((ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")) && !ntSymbolName.Contains("SENSEX"))
                    {
                        long count = Interlocked.Increment(ref _noCallbackLogCounter);
                        if (count <= 20 || count % 100 == 1)
                        {
                            Logger.Info($"[OTP-NOCB] #{count} Subscription exists but NO CALLBACKS for '{ntSymbolName}'! SubscriptionCache={_subscriptionCache.Count}, CallbackCache={_callbackCache.Count}");
                        }
                    }
                    return; // No callbacks found
                }

                // Invoke callbacks
                // CRITICAL DEBUG: Log first 10 option callback invocations
                if ((ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")) && !ntSymbolName.Contains("SENSEX"))
                {
                    long invokeCount = Interlocked.Increment(ref _optionCallbackFiredCounter);
                    if (invokeCount <= 10)
                    {
                        Logger.Info($"[OTP-INVOKE] #{invokeCount} Invoking {callbacks.Count} callbacks for '{ntSymbolName}' LTP={item.TickData.LastTradePrice}");
                    }
                }

                // Get shard-local state for this symbol (used for volume delta and price tracking)
                var symbolState = shard.GetOrCreateState(ntSymbolName);

                // Calculate volume delta using shard-local state (thread-safe, no locks)
                int volumeDelta = CalculateVolumeDelta(symbolState, item.TickData);

                // Debug logging for index symbols
                if (subscription.IsIndex && Logger.IsDebugEnabled)
                {
                    Logger.Debug($"[OTP] ProcessSingleTick INDEX: symbol={ntSymbolName}, price={item.TickData.LastTradePrice}, volumeDelta={volumeDelta}, callbacks={callbacks?.Count}");
                }

                // Process all callbacks for this subscription (pass shard-local state for price tracking)
                ProcessCallbacks(callbacks, item.TickData, volumeDelta, subscription, symbolState);

                // Record successful processing
                stopwatch.Stop();
                _performanceMonitor.RecordTickProcessed(ntSymbolName, stopwatch.ElapsedMilliseconds);

                // Record processing latency
                var latency = DateTime.UtcNow - item.QueueTime;
                _performanceMonitor.RecordProcessingLatency(ntSymbolName, latency);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log($"Error processing tick for {item?.NativeSymbolName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate volume delta using shard-local state.
        /// Thread-safe by design - only the shard worker accesses this state.
        /// Note: This updates state.PreviousVolume but NOT PreviousPrice (caller handles price update)
        /// </summary>
        private int CalculateVolumeDelta(SymbolState state, ZerodhaTickData tickData)
        {
            int volumeDelta = 0;
            if (state.PreviousVolume > 0)
            {
                volumeDelta = Math.Max(0, tickData.TotalQtyTraded - state.PreviousVolume);
            }
            else
            {
                volumeDelta = tickData.LastTradeQty > 0 ? tickData.LastTradeQty : 0;
            }

            // Update volume state (single-writer, no locks needed)
            state.PreviousVolume = tickData.TotalQtyTraded;
            state.LastTickTime = DateTime.UtcNow;

            return volumeDelta;
        }

        /// <summary>
        /// Ensure DateTime has proper Kind for NinjaTrader compatibility
        /// </summary>
        /// <summary>
        /// Process market depth updates with sharded parallelism
        /// </summary>
        private void ProcessMarketDepth(ZerodhaTickData tickData, DateTime now)
        {
            if (!_l2SubscriptionCache.TryGetValue(tickData.InstrumentIdentifier, out var l2Subscription))
                return;

            for (int index = 0; index < l2Subscription.L2Callbacks.Count; ++index)
            {
                var callback = l2Subscription.L2Callbacks.Values[index];
                
                // Process asks
                if (tickData.AskDepth != null)
                {
                    foreach (var ask in tickData.AskDepth)
                    {
                        if (ask != null && ask.Quantity > 0)
                            l2Subscription.L2Callbacks.Keys[index].UpdateMarketDepth(
                                MarketDataType.Ask, ask.Price, ask.Quantity, Operation.Update, now, callback);
                    }
                }

                // Process bids
                if (tickData.BidDepth != null)
                {
                    foreach (var bid in tickData.BidDepth)
                    {
                        if (bid != null && bid.Quantity > 0)
                            l2Subscription.L2Callbacks.Keys[index].UpdateMarketDepth(
                                MarketDataType.Bid, bid.Price, bid.Quantity, Operation.Update, now, callback);
                    }
                }
            }
        }

        // Note: EnsureNinjaTraderDateTime has been extracted to DateTimeHelper class

        /// <summary>
        /// Process callbacks for the subscription with performance tracking.
        /// Uses shard-local SymbolState for thread-safe price/volume tracking.
        /// </summary>
        private void ProcessCallbacks(List<SubscriptionCallback> callbacks, ZerodhaTickData tickData, int volumeDelta, L1Subscription subscription, SymbolState symbolState)
        {
            // For indices (no volume), check if price changed using shard-local state
            bool isIndex = subscription?.IsIndex ?? false;
            double lastPrice = tickData.LastTradePrice;
            double prevPrice = symbolState.PreviousPrice;
            bool priceChanged = isIndex && Math.Abs(lastPrice - prevPrice) > 0.0001;

            // DIAGNOSTIC: Log option tick processing to trace the flow
            string symbolName = subscription?.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            if (symbolName.Contains("CE") || symbolName.Contains("PE"))
            {
                // Log every 100th option tick to avoid spam but still get visibility
                if (Interlocked.Increment(ref _optionTickLogCounter) % 100 == 1)
                {
                    Logger.Debug($"[OTP-DIAG] Option tick: symbol={symbolName}, lastPrice={lastPrice}, volumeDelta={volumeDelta}, totalVol={tickData.TotalQtyTraded}, callbackCount={callbacks?.Count ?? 0}");
                }
            }

            // Debug logging for indices
            if (isIndex && Logger.IsDebugEnabled)
            {
                Logger.Debug($"[OTP] ProcessCallbacks INDEX: lastPrice={lastPrice}, prevPrice={prevPrice}, priceChanged={priceChanged}, volumeDelta={volumeDelta}, callbackCount={callbacks?.Count}");
            }

            // Update shard-local price state (single-writer, no locks needed)
            symbolState.PreviousPrice = lastPrice;

            // OPTIMIZATION: Get current time once before the callback loop (same timestamp for all callbacks)
            DateTime now = TimeZoneInfo.ConvertTime(DateTime.Now, IstTimeZone);

            foreach (var callbackInfo in callbacks)
            {
                try
                {
                    // RACE CONDITION FIX: Comprehensive null checks for NinjaTrader instrument hierarchy
                    if (callbackInfo == null)
                    {
                        continue;
                    }

                    if (callbackInfo.Callback == null)
                    {
                        _callbackErrors++;
                        continue;
                    }

                    // Check instrument and MasterInstrument separately to avoid null reference
                    var instrument = callbackInfo.Instrument;
                    if (instrument == null)
                    {
                        _callbackErrors++;
                        continue;
                    }

                    // MasterInstrument may not be initialized yet during startup race condition
                    var masterInstrument = instrument.MasterInstrument;
                    if (masterInstrument == null)
                    {
                        _callbackErrors++;
                        // Don't log every time - this is expected during startup
                        continue;
                    }

                    // Tick data validation
                    if (tickData.LastTradePrice <= 0 && tickData.BuyPrice <= 0 && tickData.SellPrice <= 0)
                    {
                        continue;
                    }

                    // Use the already validated variables from above
                    var callback = callbackInfo.Callback;

                    // Process last trade with timing
                    // For indices: fire callback when price changes (no volume requirement)
                    // For regular instruments: fire callback when volume changes
                    // PRE/POST MARKET: Always fire callback since we only get 1 snapshot tick per instrument
                    //                  (Zerodha sends last traded price on connect, no volume delta expected)
                    // NOTE: GIFT NIFTY & spot indices get pre-market data from 9:00 AM, others from 9:15 AM
                    bool isOutsideMarketHours = !DateTimeHelper.IsMarketOpenForSymbol(symbolName);
                    bool shouldFireCallback = (volumeDelta > 0) || (isIndex && priceChanged) || isOutsideMarketHours;

                    // Debug logging for indices or outside market hours
                    if ((isIndex || isOutsideMarketHours) && Logger.IsDebugEnabled)
                    {
                        Logger.Debug($"[OTP] Callback check: shouldFire={shouldFireCallback}, lastPrice={tickData.LastTradePrice}, volumeDelta={volumeDelta}, isIndex={isIndex}, priceChanged={priceChanged}, outsideMarket={isOutsideMarketHours}");
                    }

                    if (tickData.LastTradePrice > 0 && shouldFireCallback)
                    {
                        // DIAGNOSTIC: Log when option callback actually fires
                        if (symbolName.Contains("CE") || symbolName.Contains("PE"))
                        {
                            if (Interlocked.Increment(ref _optionCallbackFiredCounter) % 50 == 1)
                            {
                                Logger.Debug($"[OTP-DIAG] Option CALLBACK FIRED: symbol={symbolName}, price={tickData.LastTradePrice}, volumeDelta={volumeDelta}");
                            }
                        }

                        // OPTIMIZATION: Prices from Zerodha are already at valid tick sizes (exchange-traded)
                        // No need to call RoundToTickSize - removing unnecessary method calls in hot path
                        var callbackTimer = Stopwatch.StartNew();
                        callback(MarketDataType.Last, tickData.LastTradePrice, Math.Max(1, volumeDelta), now, 0L);
                        callbackTimer.Stop();

                        TrackCallbackTiming(callbackTimer.ElapsedMilliseconds);

                        // Process bid/ask only when there's a trade (or price change for indices)
                        if (tickData.BuyPrice > 0)
                        {
                            callback(MarketDataType.Bid, tickData.BuyPrice, tickData.BuyQty, now, 0L);
                        }

                        if (tickData.SellPrice > 0)
                        {
                            callback(MarketDataType.Ask, tickData.SellPrice, tickData.SellQty, now, 0L);
                        }
                    }

                    // Process daily statistics
            ProcessAdditionalMarketData(callback, instrument, tickData, now);

            // Process market depth if available
            if (tickData.HasMarketDepth)
            {
                ProcessMarketDepth(tickData, now);
            }

            // Track successful callback execution
            _callbacksExecuted++;
                }
                catch (Exception ex)
                {
                    _callbackErrors++;

                    // Only log detailed errors periodically to avoid log spam
                    if (_callbackErrors % 500 == 1)
                    {
                        Log($"CALLBACK ERROR for {tickData?.InstrumentIdentifier}: {ex.Message}");
                    }

                    // Continue processing like original working code
                    continue;
                }
            }
        }

        /// <summary>
        /// Track individual callback timing to identify NT insertion bottleneck
        /// </summary>
        private void TrackCallbackTiming(long elapsedMs)
        {
            Interlocked.Add(ref _totalCallbackTimeMs, elapsedMs);

            if (elapsedMs > 5)
            {
                Interlocked.Increment(ref _verySlowCallbacks);
            }
            else if (elapsedMs > 1)
            {
                Interlocked.Increment(ref _slowCallbacks);
            }
        }

        /// <summary>
        /// Process additional market data types efficiently
        /// OPTIMIZATION: Removed RoundToTickSize calls - prices from Zerodha are already at valid tick sizes
        /// </summary>
        private void ProcessAdditionalMarketData(Action<MarketDataType, double, long, DateTime, long> callback,
                                                Instrument instrument, ZerodhaTickData tickData, DateTime now)
        {
            if (tickData.TotalQtyTraded > 0)
            {
                callback(MarketDataType.DailyVolume, tickData.TotalQtyTraded, tickData.TotalQtyTraded, now, 0L);
            }

            if (tickData.High > 0)
            {
                callback(MarketDataType.DailyHigh, tickData.High, 0L, now, 0L);
            }

            if (tickData.Low > 0)
            {
                callback(MarketDataType.DailyLow, tickData.Low, 0L, now, 0L);
            }

            if (tickData.Open > 0)
            {
                callback(MarketDataType.Opening, tickData.Open, 0L, now, 0L);
            }

            if (tickData.Close > 0)
            {
                callback(MarketDataType.LastClose, tickData.Close, 0L, now, 0L);
            }

            if (tickData.OpenInterest > 0)
            {
                callback(MarketDataType.OpenInterest, tickData.OpenInterest, tickData.OpenInterest, now, 0L);
            }
        }

        /// <summary>
        /// Get current performance metrics
        /// </summary>
        public TickProcessorMetrics GetMetrics()
        {
            var performanceMetrics = _performanceMonitor.GetMetrics();

            return new TickProcessorMetrics
            {
                TicksQueued = Interlocked.Read(ref _ticksQueued),
                TicksProcessed = Interlocked.Read(ref _ticksProcessed),
                PendingTicks = Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed),
                SubscriptionCount = _subscriptionCache.Count,
                SymbolMappingCount = _symbolMappingCache.Count,
                IsHealthy = !_isDisposed && !_cancellationTokenSource.Token.IsCancellationRequested,
                CurrentTicksPerSecond = performanceMetrics.CurrentTicksPerSecond,
                PeakTicksPerSecond = performanceMetrics.PeakTicksPerSecond,
                AverageProcessingTimeMs = performanceMetrics.AverageProcessingTimeMs,
                ProcessingEfficiency = performanceMetrics.ProcessingEfficiency,
                TotalTicksDropped = performanceMetrics.TotalTicksDropped
            };
        }

        /// <summary>
        /// Get detailed diagnostic information about the processor state
        /// </summary>
        public string GetDiagnosticInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== OptimizedTickProcessor Diagnostic Info ===");
            sb.AppendLine($"Is Disposed: {_isDisposed}");
            sb.AppendLine($"Cancellation Requested: {_cancellationTokenSource.Token.IsCancellationRequested}");
            sb.AppendLine($"Ticks Queued: {Interlocked.Read(ref _ticksQueued)}");
            sb.AppendLine($"Ticks Processed: {Interlocked.Read(ref _ticksProcessed)}");
            sb.AppendLine($"Pending Ticks: {Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed)}");

            // Per-shard queue counts
            if (_shards != null)
            {
                for (int i = 0; i < _shards.Length; i++)
                {
                    sb.AppendLine($"Shard {i} Queue Count: {_shards[i].Queue.Count}");
                }
            }

            sb.AppendLine($"Callbacks Executed: {Interlocked.Read(ref _callbacksExecuted)}");
            sb.AppendLine($"Callback Errors: {Interlocked.Read(ref _callbackErrors)}");
            sb.AppendLine($"Slow Callbacks (>1ms): {Interlocked.Read(ref _slowCallbacks)}");
            sb.AppendLine($"Very Slow Callbacks (>5ms): {Interlocked.Read(ref _verySlowCallbacks)}");

            // Get backpressure metrics from BackpressureManager
            var bpMetrics = _backpressureManager.GetMetrics();
            sb.AppendLine($"Backpressure State: {bpMetrics.CurrentState}");
            sb.AppendLine($"Ticks Dropped (Backpressure): {bpMetrics.TicksDropped}");
            sb.AppendLine("=== End Diagnostic Info ===");

            return sb.ToString();
        }

        /// <summary>
        /// Get performance metrics for a specific symbol
        /// </summary>
        public SymbolPerformanceMetrics GetSymbolMetrics(string symbol)
        {
            return _performanceMonitor.GetSymbolMetrics(symbol);
        }

        // Note: Health monitoring has been delegated to ProcessorHealthMonitor
        // Note: Backpressure logic has been delegated to BackpressureManager

        /// <summary>
        /// Calculate optimal batch size based on current conditions
        /// </summary>
        private int CalculateOptimalBatchSize(long queueDepth)
        {
            // Under memory pressure: Use smaller batches to reduce memory allocation
            if (_isUnderMemoryPressure)
            {
                return Math.Min(20, Math.Max(5, (int)(queueDepth / 10))); // 5-20 range
            }

            // High queue depth: Use larger batches for throughput
            if (queueDepth > 1000)
            {
                return 100; // Maximum batch size for high throughput
            }
            else if (queueDepth > 500)
            {
                return 75;
            }
            else if (queueDepth > 100)
            {
                return 50;
            }
            else if (queueDepth > 20)
            {
                return 25;
            }
            else
            {
                return 10; // Small batches for low latency when queue is light
            }
        }

        private bool _enableHotPathLogging = false;

        private void Log(string message)
        {
            if (!_enableHotPathLogging) return;

            try
            {
                // Simple logging for hot-path diagnostics
                Logger.Info($"[OTP] {message}");
            }
            catch
            {
                // Ignore logging errors
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Logger.Info("[OTP] OptimizedTickProcessor: Disposing...");

            _cancellationTokenSource.Cancel();

            // Wait for shards to shut down
            if (_shards != null)
            {
                foreach (var shard in _shards)
                {
                    try
                    {
                        // CompleteAdding signals no more items will be added
                        shard.Queue.CompleteAdding();
                        shard.WorkerTask?.Wait(500);
                        shard.Queue.Dispose();
                    }
                    catch { }
                }
            }

            try
            {
                // Dispose health monitor (delegated)
                _healthMonitor?.Stop();
                _healthMonitor?.Dispose();

                _performanceMonitor?.Dispose();

                // Complete and dispose reactive stream
                try
                {
                    _tickSubject.OnCompleted();
                    _tickSubject.Dispose();
                    Logger.Debug("[OTP-RX] Reactive stream disposed");
                }
                catch { }

                // Dispose resources
                _cancellationTokenSource.Dispose();
                Logger.Info("[OTP] OptimizedTickProcessor disposed");
            }
            catch { }
        }
    }

    // Note: SubscriptionCallback, TickProcessorMetrics, and BackpressureState
    // have been extracted to ZerodhaDatafeedAdapter.Models.MarketData namespace
}
