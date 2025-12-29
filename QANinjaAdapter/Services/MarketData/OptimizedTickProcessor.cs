using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using QANinjaAdapter.Models.MarketData;
using QANinjaAdapter.Classes;
using System.Buffers;

namespace QANinjaAdapter.Services.MarketData
{
    /// <summary>
    /// High-performance tick processor that eliminates bottlenecks causing lag.
    /// Uses async queue-based processing, object pooling, and batching for optimal performance.
    ///
    /// Features:
    /// - Centralized asynchronous pipeline for all tick processing
    /// - Dedicated processing thread to avoid async/await overhead
    /// - Batch processing (configurable batch size and time windows)
    /// - Object pooling to reduce GC pressure
    /// - Intelligent backpressure management with tiered limits
    /// - O(1) callback caching for fast lookups
    /// - Memory pressure detection and automatic cache trimming
    /// </summary>
    public class OptimizedTickProcessor : IDisposable
    {
        // Sharded RingBuffer Configuration
        private const int SHARD_COUNT = 4; // Configurable based on CPU cores
        private const int RING_BUFFER_SIZE = 16384; // Must be power of 2 for fast masking
        private const int RING_BUFFER_MASK = RING_BUFFER_SIZE - 1;

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
            public readonly TickProcessingItem[] RingBuffer = new TickProcessingItem[RING_BUFFER_SIZE];
            public long Sequence = 0; // Produced count
            public long ProcessedSequence = 0; // Consumed count
            public readonly EventWaitHandle WaitHandle = new AutoResetEvent(false);
            public Task WorkerTask;

            /// <summary>
            /// Shard-local symbol state - only accessed by this shard's worker thread.
            /// Key: symbol name, Value: state (previous volume, price, etc.)
            /// No locks needed - single-writer guarantee by design.
            /// </summary>
            public readonly Dictionary<string, SymbolState> SymbolStates = new Dictionary<string, SymbolState>();

            public Shard()
            {
                for (int i = 0; i < RING_BUFFER_SIZE; i++)
                    RingBuffer[i] = new TickProcessingItem();
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

        // Backpressure Management - Tiered Queue Limits
        private readonly int _warningQueueSize;
        private readonly int _criticalQueueSize;
        private readonly int _maxAcceptableQueueSize;

        // High-performance caches for O(1) lookups
        // IMPORTANT: These are volatile references for atomic swap during updates
        // This prevents race conditions where readers see partially cleared caches
        private readonly ConcurrentDictionary<string, string> _symbolMappingCache = new ConcurrentDictionary<string, string>();
        private volatile ConcurrentDictionary<string, L1Subscription> _subscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
        private volatile ConcurrentDictionary<string, L2Subscription> _l2SubscriptionCache = new ConcurrentDictionary<string, L2Subscription>();
        private volatile ConcurrentDictionary<string, List<SubscriptionCallback>> _callbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();
        private readonly HashSet<string> _loggedMissedSymbols = new HashSet<string>(); // Track which symbols we've logged misses for

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

        // Backpressure tracking counters
        private long _ticksDroppedBackpressure = 0;
        private long _oldestTicksDropped = 0;
        private long _warningLevelEvents = 0;
        private long _criticalLevelEvents = 0;
        private DateTime _lastBackpressureEvent = DateTime.MinValue;
        private BackpressureState _currentBackpressureState = BackpressureState.Normal;

        // Health monitoring timer (30 second intervals)
        private readonly System.Timers.Timer _healthMonitoringTimer = new System.Timers.Timer(30000);
        private long _lastQueuedCount = 0;
        private long _lastProcessedCount = 0;
        private long _lastCallbacksExecuted = 0;
        private long _lastCallbackErrors = 0;
        private int _lastGC0Count = 0;
        private int _lastGC1Count = 0;
        private int _lastGC2Count = 0;

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

            // Initialize Shards
            _shards = new Shard[SHARD_COUNT];
            for (int i = 0; i < SHARD_COUNT; i++)
            {
                int shardIndex = i;
                _shards[i] = new Shard();
                _shards[i].WorkerTask = Task.Factory.StartNew(
                    () => ProcessShardSynchronously(shardIndex),
                    _cancellationTokenSource.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                );
            }

            // Initialize health monitoring
            _healthMonitoringTimer.Elapsed += LogHealthMetrics;
            _healthMonitoringTimer.AutoReset = true;
            _healthMonitoringTimer.Start();

            Logger.Info($"[OTP] OptimizedTickProcessor INITIALIZED with {SHARD_COUNT} shards, RingBuffer size={RING_BUFFER_SIZE}");
            Log($"OptimizedTickProcessor: Initialized with {SHARD_COUNT} shards and Disruptor-style RingBuffers.");
        }

        /// <summary>
        /// Process ticks for a specific shard. 
        /// Uses a busy-yield-wait strategy for minimal latency.
        /// </summary>
        private void ProcessShardSynchronously(int shardIndex)
        {
            var shard = _shards[shardIndex];
            Logger.Info($"[OTP-SHARD] Shard {shardIndex} worker STARTING");
            long lastLoggedSeq = 0;

            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Fast path: Check if there's anything to process without waiting
                    // Use Volatile.Read to ensure we see the latest producer writes
                    long currentSequence = Volatile.Read(ref shard.Sequence);
                    long processedSequence = shard.ProcessedSequence;

                    // DIAGNOSTIC: Log when sequence advances (every 100 ticks or first tick)
                    if (currentSequence > lastLoggedSeq + 100 || (currentSequence > 0 && lastLoggedSeq == 0))
                    {
                        Logger.Info($"[OTP-SHARD] Shard {shardIndex}: seq={currentSequence}, processed={processedSequence}, pending={currentSequence - processedSequence}");
                        lastLoggedSeq = currentSequence;
                    }

                    if (currentSequence == processedSequence)
                    {
                        // Wait for more data
                        shard.WaitHandle.WaitOne(100); // 100ms timeout to re-check cancellation
                        // After waking, ensure we see fresh values
                        Thread.MemoryBarrier();
                        continue;
                    }

                    // Processing loop
                    while (processedSequence < currentSequence && !_cancellationTokenSource.IsCancellationRequested)
                    {
                        int index = (int)(processedSequence & RING_BUFFER_MASK);
                        var item = shard.RingBuffer[index];

                        // Wait for producer to finish writing (spin then yield)
                        int spinCount = 0;
                        while (!item.IsReady && spinCount < 100)
                        {
                            spinCount++;
                            Thread.SpinWait(10);
                        }

                        // If still not ready after spin, yield and retry
                        int yieldCount = 0;
                        while (!item.IsReady && !_cancellationTokenSource.IsCancellationRequested)
                        {
                            yieldCount++;
                            if (yieldCount == 100) // Log if stuck for 100ms
                            {
                                Logger.Warn($"[OTP-SHARD] Shard {shardIndex}: Stuck waiting for IsReady at seq={processedSequence}, idx={index}, TickData={(item.TickData != null ? "SET" : "NULL")}");
                            }
                            Thread.Sleep(1); // Yield to let producer finish
                        }

                        // Now process the item (it must be ready or we're cancelled)
                        if (!_cancellationTokenSource.IsCancellationRequested && item.TickData != null)
                        {
                            try
                            {
                                ProcessSingleTick(item, shard);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[OTP-SHARD] Shard {shardIndex}: Error processing tick: {ex.Message}", ex);
                            }
                            finally
                            {
                                // Item cleanup for reuse (Disruptor pattern) - also clears IsReady
                                item.Reset();
                            }
                        }
                        else if (!item.IsReady)
                        {
                            // Item never became ready - log this anomaly
                            Logger.Warn($"[OTP-SHARD] Shard {shardIndex}: Item at index {index} never became ready (seq={processedSequence})");
                        }
                        else if (item.TickData == null)
                        {
                            // IsReady was set but TickData is null - data corruption
                            Logger.Warn($"[OTP-SHARD] Shard {shardIndex}: IsReady=true but TickData=NULL at seq={processedSequence}, idx={index}");
                        }

                        processedSequence++;
                        shard.ProcessedSequence = processedSequence;
                        Interlocked.Increment(ref _ticksProcessed);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CRITICAL: Shard {shardIndex} failed: {ex.Message}");
            }
            finally
            {
                Log($"OptimizedTickProcessor: Shard {shardIndex} worker ended");
            }
        }

        /// <summary>
        /// Queues a tick for processing (non-blocking).
        /// Uses object pooling to reduce GC pressure.
        /// Implements intelligent backpressure management with tiered limits.
        /// </summary>
        public bool QueueTick(string nativeSymbolName, ZerodhaTickData tickData)
        {
            if (_isDisposed) return false;

            // Record tick received
            _performanceMonitor.RecordTickReceived(nativeSymbolName);

            // Shard selection based on symbol hash
            int shardIndex = (nativeSymbolName.GetHashCode() & 0x7FFFFFFF) % SHARD_COUNT;
            var shard = _shards[shardIndex];

            // Backpressure check
            long currentCount = Interlocked.Read(ref shard.Sequence) - shard.ProcessedSequence;
            if (currentCount >= RING_BUFFER_SIZE - 2048) // Safety margin before full
            {
                var backpressureResult = ApplyBackpressureManagement(currentCount, null, nativeSymbolName);
                if (!backpressureResult.shouldQueue)
                {
                    _performanceMonitor.RecordTickDropped(nativeSymbolName);
                    return false;
                }
            }

            // ATOMIC: Reserve slot using Increment (returns NEW value, so subtract 1 for index)
            // This fixes the race condition where multiple threads would read the same sequence
            long sequence = Interlocked.Increment(ref shard.Sequence) - 1;
            int index = (int)(sequence & RING_BUFFER_MASK);

            // DIAGNOSTIC: Log first few queued ticks per shard
            if (sequence < 5)
            {
                Logger.Info($"[OTP-QUEUE] Shard {shardIndex}: Queuing tick seq={sequence}, idx={index}, symbol={nativeSymbolName}");
            }

            // Assign data directly to pre-allocated item
            var item = shard.RingBuffer[index];
            item.NativeSymbolName = nativeSymbolName;
            item.TickData = tickData;
            item.QueueTime = DateTime.UtcNow;

            // Mark item as ready for consumer (ensures data is visible before consumer reads)
            item.MarkReady();

            // Ensure all writes are visible before signaling
            Thread.MemoryBarrier();

            // Signal worker (sequence already incremented above)
            Interlocked.Increment(ref _ticksQueued);
            shard.WaitHandle.Set();

            return true;
        }

        /// <summary>
        /// Intelligent backpressure management with tiered limits
        /// </summary>
        private (bool shouldQueue, string reason) ApplyBackpressureManagement(long currentQueueDepth, TickProcessingItem item, string symbol)
        {
            // Update backpressure state
            var newState = DetermineBackpressureState(currentQueueDepth);
            if (newState != _currentBackpressureState)
            {
                var previousState = _currentBackpressureState;
                _currentBackpressureState = newState;
                _lastBackpressureEvent = DateTime.UtcNow;

                Log($"BACKPRESSURE STATE CHANGE: {previousState} -> {newState} (Queue: {currentQueueDepth})");

                // Increment event counters
                if (newState == BackpressureState.Warning) Interlocked.Increment(ref _warningLevelEvents);
                if (newState == BackpressureState.Critical) Interlocked.Increment(ref _criticalLevelEvents);
            }

            switch (_currentBackpressureState)
            {
                case BackpressureState.Normal:
                    return (true, "Normal processing");

                case BackpressureState.Warning:
                    // At warning level: Apply selective dropping for low-priority symbols
                    if (IsLowPrioritySymbol(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Dropped low-priority symbol {symbol} at warning level");
                    }
                    return (true, "Warning level - high priority symbols only");

                case BackpressureState.Critical:
                    // At critical level: Apply aggressive dropping and oldest tick removal
                    if (ShouldDropTickAtCriticalLevel(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Dropped symbol {symbol} at critical level");
                    }

                    return (true, "Critical level - accepting high priority symbol");

                case BackpressureState.Emergency:
                    // At emergency level: Drop all but essential ticks
                    if (!IsEssentialSymbol(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Emergency: Only essential symbols accepted, dropped {symbol}");
                    }

                    return (true, "Emergency level - essential symbol accepted");

                default:
                    // Absolute maximum: reject everything
                    Interlocked.Increment(ref _ticksDroppedBackpressure);
                    return (false, $"Queue at maximum capacity ({currentQueueDepth}), rejected {symbol}");
            }
        }

        /// <summary>
        /// Determine backpressure state based on queue depth
        /// </summary>
        private BackpressureState DetermineBackpressureState(long queueDepth)
        {
            if (queueDepth >= _maxQueueSize) return BackpressureState.Maximum;
            if (queueDepth >= _maxAcceptableQueueSize) return BackpressureState.Emergency;
            if (queueDepth >= _criticalQueueSize) return BackpressureState.Critical;
            if (queueDepth >= _warningQueueSize) return BackpressureState.Warning;
            return BackpressureState.Normal;
        }


        /// <summary>
        /// Determine if symbol is low priority for dropping
        /// </summary>
        private bool IsLowPrioritySymbol(string symbol)
        {
            return string.IsNullOrEmpty(symbol) ||
                   symbol.Contains("TEST") ||
                   symbol.Contains("DEMO");
        }

        /// <summary>
        /// Determine if tick should be dropped at critical level
        /// </summary>
        private bool ShouldDropTickAtCriticalLevel(string symbol)
        {
            // At critical level, be more aggressive - drop 30% of non-essential ticks
            return !IsEssentialSymbol(symbol) && (symbol.GetHashCode() % 10 < 3);
        }

        /// <summary>
        /// Determine if symbol is essential and should never be dropped
        /// </summary>
        private bool IsEssentialSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            // Major indices and high-volume stocks might be essential
            return symbol.Contains("NIFTY") ||
                   symbol.Contains("SENSEX") ||
                   symbol.Contains("BANKNIFTY");
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

            // CRITICAL DEBUG: Log cache update
            if (subscriptions.Count < 2 && subscriptions.Count > 0)
            {
                 // Suspicious: updating cache with very few items?
                 Logger.Info($"[OTP-CACHE] UpdateSubscriptionCache called with only {subscriptions.Count} items! Possible cache stomp.");
                 foreach(var key in subscriptions.Keys) Logger.Info($"[OTP-CACHE] Key: {key}");
            }
            else
            {
                 Logger.Info($"[OTP-CACHE] UpdateSubscriptionCache called with {subscriptions.Count} items.");
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
                Logger.Warn($"[OTP] Skipped {skippedUninitializedCount} uninitialized instrument callbacks");
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
                        Logger.Info($"[OTP-ENTRY] ProcessSingleTick: {ntSymbolName}, LTP={item.TickData.LastTradePrice}, Vol={item.TickData.TotalQtyTraded}");
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

                // Fast subscription lookup using cache (O(1))
                if (!_subscriptionCache.TryGetValue(ntSymbolName, out var subscription))
                {
                    // Log first miss for each symbol to help debug - include NIFTY options
                    if (_subscriptionCache.Count > 0 && !_loggedMissedSymbols.Contains(ntSymbolName))
                    {
                        // Log NIFTY/SENSEX option misses
                        if ((ntSymbolName.Contains("NIFTY") || ntSymbolName.Contains("SENSEX")) &&
                            (ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")))
                        {
                            _loggedMissedSymbols.Add(ntSymbolName);
                            Logger.Info($"[OTP-MISS] No subscription for option '{ntSymbolName}'. SubscriptionCache has {_subscriptionCache.Count} entries. First 5: {string.Join(", ", _subscriptionCache.Keys.Take(5))}");
                        }
                    }

                    return; // No subscription found
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
                    // DIAGNOSTIC: Log when subscription exists but no callbacks (every 100th occurrence)
                    if ((ntSymbolName.Contains("CE") || ntSymbolName.Contains("PE")) && !ntSymbolName.Contains("SENSEX"))
                    {
                        if (Interlocked.Increment(ref _noCallbackLogCounter) % 100 == 1)
                        {
                            Logger.Info($"[OTP-NOCB] Subscription exists but NO CALLBACKS for {ntSymbolName}! SubscriptionCache={_subscriptionCache.Count}, CallbackCache={_callbackCache.Count}");
                        }
                    }
                    return; // No callbacks found
                }

                // Invoke callbacks
                // CRITICAL DEBUG: Log invocation
                if (ntSymbolName.Contains("SENSEX") && ntSymbolName.Contains("CE") && item.TickData.LastTradePrice > 0)
                {
                     if (DateTime.Now.Millisecond < 50) // sample
                        Logger.Info($"[OTP-CALLBACK] Invoking {callbacks.Count} callbacks for {ntSymbolName}");
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

        private DateTime EnsureNinjaTraderDateTime(DateTime dateTime)
        {
            try
            {
                // Optimization: Case-by-case handling without expensive TimeZoneInfo.FindSystemTimeZoneById calls
                if (dateTime.Kind == DateTimeKind.Local)
                {
                    return dateTime; // Already local
                }

                // Handle UTC DateTime - convert to cached IST
                if (dateTime.Kind == DateTimeKind.Utc)
                {
                    var istTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, IstTimeZone);
                    return DateTime.SpecifyKind(istTime, DateTimeKind.Local);
                }

                // Handle Unspecified DateTime - assume it's local IST
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Local);
            }
            catch
            {
                // Fallback: Create DateTime with current time
                return DateTime.Now;
            }
        }

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
                    Logger.Info($"[OTP-DIAG] Option tick: symbol={symbolName}, lastPrice={lastPrice}, volumeDelta={volumeDelta}, totalVol={tickData.TotalQtyTraded}, callbackCount={callbacks?.Count ?? 0}");
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
                    bool shouldFireCallback = (volumeDelta > 0) || (isIndex && priceChanged);

                    // Debug logging for indices
                    if (isIndex && Logger.IsDebugEnabled)
                    {
                        Logger.Debug($"[OTP] INDEX callback check: shouldFire={shouldFireCallback}, lastPrice={tickData.LastTradePrice}, volumeDelta={volumeDelta}, isIndex={isIndex}, priceChanged={priceChanged}");
                    }

                    if (tickData.LastTradePrice > 0 && shouldFireCallback)
                    {
                        // DIAGNOSTIC: Log when option callback actually fires
                        if (symbolName.Contains("CE") || symbolName.Contains("PE"))
                        {
                            if (Interlocked.Increment(ref _optionCallbackFiredCounter) % 50 == 1)
                            {
                                Logger.Info($"[OTP-DIAG] Option CALLBACK FIRED: symbol={symbolName}, price={tickData.LastTradePrice}, volumeDelta={volumeDelta}");
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
            sb.AppendLine($"Callbacks Executed: {Interlocked.Read(ref _callbacksExecuted)}");
            sb.AppendLine($"Callback Errors: {Interlocked.Read(ref _callbackErrors)}");
            sb.AppendLine($"Slow Callbacks (>1ms): {Interlocked.Read(ref _slowCallbacks)}");
            sb.AppendLine($"Very Slow Callbacks (>5ms): {Interlocked.Read(ref _verySlowCallbacks)}");
            sb.AppendLine($"Backpressure State: {_currentBackpressureState}");
            sb.AppendLine($"Ticks Dropped (Backpressure): {Interlocked.Read(ref _ticksDroppedBackpressure)}");
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

        /// <summary>
        /// Comprehensive health monitoring (every 30 seconds)
        /// </summary>
        private void LogHealthMetrics(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Current metrics
                var currentQueued = Interlocked.Read(ref _ticksQueued);
                var currentProcessed = Interlocked.Read(ref _ticksProcessed);
                var currentCallbacksExecuted = Interlocked.Read(ref _callbacksExecuted);
                var currentCallbackErrors = Interlocked.Read(ref _callbackErrors);

                // Calculate rates (per 30 seconds)
                var queuedRate = currentQueued - _lastQueuedCount;
                var processedRate = currentProcessed - _lastProcessedCount;
                var callbacksRate = currentCallbacksExecuted - _lastCallbacksExecuted;
                var errorsRate = currentCallbackErrors - _lastCallbackErrors;

                // Queue health
                var queueDepth = currentQueued - currentProcessed;
                var processingEfficiency = queuedRate > 0 ? (processedRate * 100.0 / queuedRate) : 100.0;

                // Callback health
                var totalCallbacks = currentCallbacksExecuted + currentCallbackErrors;
                var callbackSuccessRate = totalCallbacks > 0 ? (currentCallbacksExecuted * 100.0 / totalCallbacks) : 0.0;

                // Memory metrics
                var currentGC0 = GC.CollectionCount(0);
                var currentGC1 = GC.CollectionCount(1);
                var currentGC2 = GC.CollectionCount(2);
                var gc0Rate = currentGC0 - _lastGC0Count;
                var gc1Rate = currentGC1 - _lastGC1Count;
                var gc2Rate = currentGC2 - _lastGC2Count;
                var totalMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;

                // Backpressure metrics
                var ticksDroppedBP = Interlocked.Read(ref _ticksDroppedBackpressure);
                var oldestDropped = Interlocked.Read(ref _oldestTicksDropped);
                var warningEvents = Interlocked.Read(ref _warningLevelEvents);
                var criticalEvents = Interlocked.Read(ref _criticalLevelEvents);

                // Callback timing metrics
                var avgCallbackTime = currentCallbacksExecuted > 0 ?
                    (double)Interlocked.Read(ref _totalCallbackTimeMs) / currentCallbacksExecuted : 0.0;
                var slowCallbacks = Interlocked.Read(ref _slowCallbacks);
                var verySlowCallbacks = Interlocked.Read(ref _verySlowCallbacks);
                var slowCallbackPercentage = currentCallbacksExecuted > 0 ?
                    (slowCallbacks * 100.0 / currentCallbacksExecuted) : 0.0;

                // Determine overall health status
                var healthStatus = AssessHealthStatus(queueDepth, callbackSuccessRate, processingEfficiency, gc2Rate);

                Log(
                    $"HEALTH REPORT {healthStatus}\n" +
                    $"   Queue: {queueDepth} pending | {queuedRate}/30s in, {processedRate}/30s out | {processingEfficiency:F1}% efficiency\n" +
                    $"   Callbacks: {callbacksRate}/30s | {callbackSuccessRate:F1}% success | {avgCallbackTime:F3}ms avg | {slowCallbackPercentage:F1}% slow (>1ms)\n" +
                    $"   Backpressure: {_currentBackpressureState} | {ticksDroppedBP} dropped | {oldestDropped} aged out | Events: W{warningEvents}/C{criticalEvents}\n" +
                    $"   Memory: {totalMemoryMB}MB | GC: {gc0Rate}/0, {gc1Rate}/1, {gc2Rate}/2\n" +
                    $"   Totals: {currentQueued:N0} queued, {currentProcessed:N0} processed, {currentCallbacksExecuted:N0} callbacks");

                // Issue health alerts if needed
                LogHealthAlerts(queueDepth, callbackSuccessRate, processingEfficiency, gc2Rate, totalMemoryMB);

                // Update previous counts for next iteration
                _lastQueuedCount = currentQueued;
                _lastProcessedCount = currentProcessed;
                _lastCallbacksExecuted = currentCallbacksExecuted;
                _lastCallbackErrors = currentCallbackErrors;
                _lastGC0Count = currentGC0;
                _lastGC1Count = currentGC1;
                _lastGC2Count = currentGC2;
            }
            catch (Exception ex)
            {
                Log($"Error in health monitoring: {ex.Message}");
            }
        }

        /// <summary>
        /// Assess overall health status based on key metrics
        /// </summary>
        private string AssessHealthStatus(long queueDepth, double callbackSuccessRate, double processingEfficiency, int gc2Rate)
        {
            // Critical issues
            if (_currentBackpressureState >= BackpressureState.Emergency || queueDepth > _criticalQueueSize ||
                callbackSuccessRate < 95.0 || processingEfficiency < 80.0 || gc2Rate > 2)
            {
                return "CRITICAL";
            }

            // Warning issues
            if (_currentBackpressureState >= BackpressureState.Warning || queueDepth > _warningQueueSize ||
                callbackSuccessRate < 98.0 || processingEfficiency < 95.0 || gc2Rate > 1)
            {
                return "WARNING";
            }

            // All good
            return "HEALTHY";
        }

        /// <summary>
        /// Log specific health alerts for actionable issues
        /// </summary>
        private void LogHealthAlerts(long queueDepth, double callbackSuccessRate, double processingEfficiency, int gc2Rate, long memoryMB)
        {
            var alerts = new List<string>();

            // Backpressure alerts
            if (_currentBackpressureState >= BackpressureState.Warning)
            {
                alerts.Add($"BACKPRESSURE {_currentBackpressureState}: Queue depth {queueDepth} exceeded threshold");
            }

            // Queue depth alerts
            if (queueDepth > _criticalQueueSize)
            {
                alerts.Add($"CRITICAL QUEUE DEPTH: {queueDepth} items pending (threshold: {_criticalQueueSize})");
            }
            else if (queueDepth > _warningQueueSize)
            {
                alerts.Add($"HIGH QUEUE DEPTH: {queueDepth} items pending (threshold: {_warningQueueSize})");
            }

            // Callback performance alerts
            if (callbackSuccessRate < 95.0)
            {
                alerts.Add($"LOW CALLBACK SUCCESS: {callbackSuccessRate:F1}% (target: >95%)");
            }

            // Processing efficiency alerts
            if (processingEfficiency < 80.0)
            {
                alerts.Add($"LOW PROCESSING EFFICIENCY: {processingEfficiency:F1}% (target: >95%)");
            }

            // Memory pressure alerts
            if (gc2Rate > 2)
            {
                alerts.Add($"HIGH GC PRESSURE: {gc2Rate} Gen2 collections in 30s (target: <=1)");
            }

            if (memoryMB > 500)
            {
                alerts.Add($"HIGH MEMORY USAGE: {memoryMB}MB (consider monitoring)");
            }

            // Log all alerts
            foreach (var alert in alerts)
            {
                Log($"ALERT: {alert}");
            }
        }

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

            Log("OptimizedTickProcessor: Disposing...");

            _cancellationTokenSource.Cancel();

            // Wait for shards to shut down
            if (_shards != null)
            {
                foreach (var shard in _shards)
                {
                    shard.WaitHandle.Set(); // Signal to exit
                    try
                    {
                        shard.WorkerTask?.Wait(500);
                        shard.WaitHandle.Dispose();
                    }
                    catch { }
                }
            }

            try
            {
                _performanceMonitor?.Dispose();

                // Dispose resources
                _cancellationTokenSource.Dispose();
                Log("OptimizedTickProcessor disposed");
            }
            catch { }
        }
    }

    /// <summary>
    /// Cached subscription callback information for O(1) lookup
    /// </summary>
    public class SubscriptionCallback
    {
        public Instrument Instrument { get; set; }
        public Action<MarketDataType, double, long, DateTime, long> Callback { get; set; }
    }

    /// <summary>
    /// Performance metrics for the tick processor
    /// </summary>
    public class TickProcessorMetrics
    {
        public long TicksQueued { get; set; }
        public long TicksProcessed { get; set; }
        public long PendingTicks { get; set; }
        public int SubscriptionCount { get; set; }
        public int SymbolMappingCount { get; set; }
        public bool IsHealthy { get; set; }
        public long CurrentTicksPerSecond { get; set; }
        public long PeakTicksPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double ProcessingEfficiency { get; set; }
        public long TotalTicksDropped { get; set; }
    }

    /// <summary>
    /// Backpressure management states for intelligent queue control
    /// </summary>
    public enum BackpressureState
    {
        /// <summary>Normal operation - all ticks accepted</summary>
        Normal = 0,

        /// <summary>Warning level - selective dropping of low-priority symbols</summary>
        Warning = 1,

        /// <summary>Critical level - aggressive dropping and oldest tick removal</summary>
        Critical = 2,

        /// <summary>Emergency level - only essential symbols accepted</summary>
        Emergency = 3,

        /// <summary>Maximum capacity - reject all new ticks</summary>
        Maximum = 4
    }
}
