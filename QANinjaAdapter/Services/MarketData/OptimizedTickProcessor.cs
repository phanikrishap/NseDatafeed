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
        private readonly ConcurrentQueue<TickProcessingItem> _tickQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _processingTask;
        private readonly SemaphoreSlim _queueSemaphore;
        private readonly int _maxQueueSize;

        // Backpressure Management - Tiered Queue Limits
        private readonly int _warningQueueSize;
        private readonly int _criticalQueueSize;
        private readonly int _maxAcceptableQueueSize;

        // High-performance caches for O(1) lookups
        private readonly ConcurrentDictionary<string, string> _symbolMappingCache = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, L1Subscription> _subscriptionCache = new ConcurrentDictionary<string, L1Subscription>();
        private readonly ConcurrentDictionary<string, List<SubscriptionCallback>> _callbackCache = new ConcurrentDictionary<string, List<SubscriptionCallback>>();

        // Object pooling to reduce GC pressure
        private readonly ConcurrentBag<TickProcessingItem> _tickItemPool = new ConcurrentBag<TickProcessingItem>();
        private readonly ConcurrentBag<List<TickProcessingItem>> _batchPool = new ConcurrentBag<List<TickProcessingItem>>();
        private long _poolHits = 0;
        private long _poolMisses = 0;

        // Pool size limits to prevent unbounded growth
        private const int MAX_TICK_ITEM_POOL_SIZE = 1000;
        private const int MAX_BATCH_POOL_SIZE = 50;
        private volatile int _currentTickItemPoolSize = 0;
        private volatile int _currentBatchPoolSize = 0;

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
            // Warning at 60% capacity, Critical at 80%, Max acceptable at 90%
            _warningQueueSize = (int)(maxQueueSize * 0.6);      // 12,000 for default 20,000
            _criticalQueueSize = (int)(maxQueueSize * 0.8);     // 16,000 for default 20,000
            _maxAcceptableQueueSize = (int)(maxQueueSize * 0.9); // 18,000 for default 20,000

            _tickQueue = new ConcurrentQueue<TickProcessingItem>();
            _queueSemaphore = new SemaphoreSlim(0);
            _cancellationTokenSource = new CancellationTokenSource();

            // Initialize performance monitoring
            _performanceMonitor = new PerformanceMonitor(TimeSpan.FromSeconds(30));

            // Initialize health monitoring system
            _healthMonitoringTimer.Elapsed += LogHealthMetrics;
            _healthMonitoringTimer.AutoReset = true;
            _healthMonitoringTimer.Start();

            Log($"OptimizedTickProcessor: Starting initialization with backpressure limits - Warning: {_warningQueueSize}, Critical: {_criticalQueueSize}, Max: {_maxAcceptableQueueSize}");

            // Use Task.Factory.StartNew with LongRunning for dedicated thread
            _processingTask = Task.Factory.StartNew(
                ProcessTicksSynchronously,
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );

            Log($"OptimizedTickProcessor initialized with health monitoring (30s intervals). Task Status: {_processingTask.Status}, Task ID: {_processingTask.Id}");
        }

        /// <summary>
        /// Synchronous processing method using dedicated thread for .NET Framework 4.8 compatibility.
        /// Uses object pooling for batch processing.
        /// </summary>
        private void ProcessTicksSynchronously()
        {
            var batchBuffer = GetPooledBatch();

            Log("OptimizedTickProcessor: Starting processing loop");

            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Use synchronous wait instead of async
                    bool signaled = _queueSemaphore.Wait(1000, _cancellationTokenSource.Token); // 1 second timeout

                    if (!signaled)
                    {
                        continue; // Timeout, check cancellation and continue
                    }

                    // Dequeue ticks efficiently
                    while (_tickQueue.TryDequeue(out var tick))
                    {
                        batchBuffer.Add(tick);

                        // Dynamic batch sizing based on queue depth and memory pressure
                        var queueDepth = Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed);
                        int targetBatchSize = CalculateOptimalBatchSize(queueDepth);
                        int maxProcessingDelay = _isUnderMemoryPressure ? 5 : 10; // Reduce delay under pressure

                        // Process in batches for better performance
                        if (batchBuffer.Count >= targetBatchSize ||
                            (batchBuffer.Count > 0 && (DateTime.UtcNow - batchBuffer[0].QueueTime).TotalMilliseconds > maxProcessingDelay))
                        {
                            ProcessTickBatch(batchBuffer);
                            // Return items to pool and clear buffer
                            ReturnBatchToPool(batchBuffer);
                            batchBuffer = GetPooledBatch();
                        }
                    }

                    // Process any remaining items in buffer after a short delay
                    if (batchBuffer.Count > 0)
                    {
                        // Reduce sleep time under memory pressure or high queue depth
                        var queueDepth = Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed);
                        int sleepTime = (_isUnderMemoryPressure || queueDepth > 100) ? 1 : 5;
                        Thread.Sleep(sleepTime);

                        if (batchBuffer.Count > 0)
                        {
                            ProcessTickBatch(batchBuffer);
                            ReturnBatchToPool(batchBuffer);
                            batchBuffer = GetPooledBatch();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                Log("OptimizedTickProcessor: Processing loop canceled");
            }
            catch (Exception ex)
            {
                Log($"CRITICAL: OptimizedTickProcessor processing loop failed: {ex.Message}");
            }
            finally
            {
                // Process any remaining ticks
                if (batchBuffer.Count > 0)
                {
                    ProcessTickBatch(batchBuffer);
                }
                // Return final batch to pool
                ReturnBatchToPool(batchBuffer);
                Log("OptimizedTickProcessor: Processing loop ended");
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

            // Get item from pool or create new one
            var item = GetPooledTickItem();
            item.NativeSymbolName = nativeSymbolName;
            item.TickData = tickData;
            item.QueueTime = DateTime.UtcNow;

            // Record tick received for performance monitoring
            _performanceMonitor.RecordTickReceived(nativeSymbolName);

            // Intelligent backpressure management
            var currentCount = Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed);
            var backpressureResult = ApplyBackpressureManagement(currentCount, item, nativeSymbolName);

            if (!backpressureResult.shouldQueue)
            {
                // Return item to pool before dropping
                ReturnTickItemToPool(item);

                // Record the drop for monitoring
                _performanceMonitor.RecordTickDropped(nativeSymbolName);

                return false;
            }

            _tickQueue.Enqueue(item);
            Interlocked.Increment(ref _ticksQueued);

            // Signal that a new item is available
            _queueSemaphore.Release();

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

                    // Remove oldest ticks to make room
                    DropOldestTicks(Math.Min(50, (int)(currentQueueDepth * 0.1))); // Drop up to 10% or 50 ticks
                    return (true, "Critical level - oldest ticks dropped");

                case BackpressureState.Emergency:
                    // At emergency level: Drop all but essential ticks
                    if (!IsEssentialSymbol(symbol))
                    {
                        Interlocked.Increment(ref _ticksDroppedBackpressure);
                        return (false, $"Emergency: Only essential symbols accepted, dropped {symbol}");
                    }

                    // Aggressively remove oldest ticks
                    DropOldestTicks(Math.Min(100, (int)(currentQueueDepth * 0.2))); // Drop up to 20% or 100 ticks
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
        /// Drop oldest ticks from queue to reduce pressure
        /// </summary>
        private void DropOldestTicks(int maxToDrop)
        {
            var dropped = 0;
            var tempList = new List<TickProcessingItem>();

            // Dequeue items and keep only newer ones
            while (dropped < maxToDrop && _tickQueue.TryDequeue(out var oldestItem))
            {
                var age = DateTime.UtcNow - oldestItem.QueueTime;
                if (age.TotalMilliseconds > 100) // Drop ticks older than 100ms
                {
                    ReturnTickItemToPool(oldestItem);
                    dropped++;
                    Interlocked.Increment(ref _oldestTicksDropped);
                }
                else
                {
                    tempList.Add(oldestItem); // Keep newer items
                }
            }

            // Re-enqueue the items we want to keep
            foreach (var item in tempList)
            {
                _tickQueue.Enqueue(item);
            }

            if (dropped > 0)
            {
                Log($"BACKPRESSURE: Dropped {dropped} oldest ticks (>100ms age)");
            }
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

        /// <summary>
        /// Get a tick processing item from pool or create new one
        /// </summary>
        private TickProcessingItem GetPooledTickItem()
        {
            if (_tickItemPool.TryTake(out var item))
            {
                Interlocked.Increment(ref _poolHits);
                Interlocked.Decrement(ref _currentTickItemPoolSize);
                return item;
            }

            Interlocked.Increment(ref _poolMisses);
            return new TickProcessingItem();
        }

        /// <summary>
        /// Return tick processing item to pool for reuse
        /// </summary>
        private void ReturnTickItemToPool(TickProcessingItem item)
        {
            if (item == null) return;

            // Clear references for GC
            item.Reset();

            // Only add to pool if under size limit
            if (_currentTickItemPoolSize < MAX_TICK_ITEM_POOL_SIZE)
            {
                _tickItemPool.Add(item);
                Interlocked.Increment(ref _currentTickItemPoolSize);
            }
            // If pool is full, let GC collect the item instead of pooling it
        }

        /// <summary>
        /// Get a batch list from pool or create new one
        /// </summary>
        private List<TickProcessingItem> GetPooledBatch()
        {
            if (_batchPool.TryTake(out var batch))
            {
                batch.Clear(); // Ensure it's empty
                Interlocked.Decrement(ref _currentBatchPoolSize);
                return batch;
            }

            // Reduce initial capacity under memory pressure
            int initialCapacity = _isUnderMemoryPressure ? 25 : 100;
            return new List<TickProcessingItem>(initialCapacity);
        }

        /// <summary>
        /// Return batch list to pool for reuse
        /// </summary>
        private void ReturnBatchToPool(List<TickProcessingItem> batch)
        {
            if (batch == null) return;

            // Return individual items to their pool
            foreach (var item in batch)
            {
                ReturnTickItemToPool(item);
            }

            batch.Clear();

            // Only pool if under size limit and reasonable capacity
            if (_currentBatchPoolSize < MAX_BATCH_POOL_SIZE &&
                batch.Capacity <= (_isUnderMemoryPressure ? 100 : 200))
            {
                _batchPool.Add(batch);
                Interlocked.Increment(ref _currentBatchPoolSize);
            }
            // If pool is full or batch is too large, let GC collect it
        }

        /// <summary>
        /// Updates subscription cache (thread-safe)
        /// </summary>
        public void UpdateSubscriptionCache(ConcurrentDictionary<string, L1Subscription> subscriptions)
        {
            lock (_cacheUpdateLock)
            {
                // Check memory pressure before expensive cache update
                CheckMemoryPressure();

                _subscriptionCache.Clear();
                _callbackCache.Clear();

                // Limit cache size under memory pressure
                int maxCacheSize = _isUnderMemoryPressure ? 500 : 2000;
                int itemsProcessed = 0;

                foreach (var kvp in subscriptions)
                {
                    if (itemsProcessed >= maxCacheSize)
                    {
                        Log($"MEMORY PRESSURE: Cache size limited to {maxCacheSize} items (total subscriptions: {subscriptions.Count})");
                        break;
                    }

                    _subscriptionCache[kvp.Key] = kvp.Value;

                    // Pre-build callback list for O(1) retrieval during tick processing
                    var callbacks = new List<SubscriptionCallback>();
                    foreach (var callbackPair in kvp.Value.L1Callbacks)
                    {
                        callbacks.Add(new SubscriptionCallback
                        {
                            Instrument = callbackPair.Key,
                            Callback = callbackPair.Value
                        });
                    }
                    _callbackCache[kvp.Key] = callbacks;
                    itemsProcessed++;
                }

                Log($"OptimizedTickProcessor: Updated caches - {_subscriptionCache.Count} subscriptions (memory pressure: {_isUnderMemoryPressure})");

                // Force GC if under pressure and cache is large
                if (_isUnderMemoryPressure && _subscriptionCache.Count > 100)
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }
            }
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
        /// Process a batch of ticks efficiently
        /// </summary>
        private void ProcessTickBatch(List<TickProcessingItem> batch)
        {
            // Process all ticks in the batch
            foreach (var item in batch)
            {
                try
                {
                    ProcessSingleTick(item);
                }
                catch (Exception ex)
                {
                    // Only log critical processing errors
                    Log($"ProcessTickBatch: Error processing tick for {item.NativeSymbolName}: {ex.Message}");
                }
            }

            Interlocked.Add(ref _ticksProcessed, batch.Count);
        }

        /// <summary>
        /// Process a single tick with optimized lookups and performance tracking
        /// </summary>
        private void ProcessSingleTick(TickProcessingItem item)
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
                    return; // No subscription found
                }

                // Fast callback lookup using pre-built cache (O(1))
                if (!_callbackCache.TryGetValue(ntSymbolName, out var callbacks) || callbacks?.Count == 0)
                {
                    return; // No callbacks found
                }

                // Calculate volume delta efficiently
                int volumeDelta = CalculateVolumeDelta(subscription, item.TickData);

                // Process all callbacks for this subscription
                ProcessCallbacks(callbacks, item.TickData, volumeDelta);

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
        /// Calculate volume delta efficiently
        /// </summary>
        private int CalculateVolumeDelta(L1Subscription subscription, ZerodhaTickData tickData)
        {
            int volumeDelta = 0;
            if (subscription.PreviousVolume > 0)
            {
                volumeDelta = Math.Max(0, tickData.TotalQtyTraded - subscription.PreviousVolume);
            }
            else
            {
                volumeDelta = tickData.LastTradeQty > 0 ? tickData.LastTradeQty : 0;
            }
            subscription.PreviousVolume = tickData.TotalQtyTraded;
            return volumeDelta;
        }

        /// <summary>
        /// Ensure DateTime has proper Kind for NinjaTrader compatibility
        /// </summary>
        private DateTime EnsureNinjaTraderDateTime(DateTime dateTime)
        {
            try
            {
                if (dateTime.Kind == DateTimeKind.Local)
                {
                    return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day,
                                      dateTime.Hour, dateTime.Minute, dateTime.Second,
                                      dateTime.Millisecond, DateTimeKind.Local);
                }

                // Handle UTC DateTime - convert to IST
                if (dateTime.Kind == DateTimeKind.Utc)
                {
                    TimeZoneInfo istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                    var istTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, istTimeZone);
                    return new DateTime(istTime.Year, istTime.Month, istTime.Day,
                                      istTime.Hour, istTime.Minute, istTime.Second,
                                      istTime.Millisecond, DateTimeKind.Local);
                }

                // Handle Unspecified DateTime - assume it's local IST
                return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day,
                                  dateTime.Hour, dateTime.Minute, dateTime.Second,
                                  dateTime.Millisecond, DateTimeKind.Local);
            }
            catch
            {
                // Fallback: Create DateTime with current time
                var now = DateTime.Now;
                return new DateTime(now.Year, now.Month, now.Day,
                                  now.Hour, now.Minute, now.Second,
                                  now.Millisecond, DateTimeKind.Local);
            }
        }

        /// <summary>
        /// Process callbacks for the subscription with performance tracking
        /// </summary>
        private void ProcessCallbacks(List<SubscriptionCallback> callbacks, ZerodhaTickData tickData, int volumeDelta)
        {
            foreach (var callbackInfo in callbacks)
            {
                try
                {
                    // Callback validation
                    if (callbackInfo?.Callback == null || callbackInfo.Instrument == null ||
                        callbackInfo.Instrument.MasterInstrument == null)
                    {
                        _callbackErrors++;
                        continue;
                    }

                    // Tick data validation
                    if (tickData.LastTradePrice <= 0 && tickData.BuyPrice <= 0 && tickData.SellPrice <= 0)
                    {
                        continue;
                    }

                    var instrument = callbackInfo.Instrument;
                    var callback = callbackInfo.Callback;

                    // Get current time in IST
                    DateTime now = DateTime.Now;
                    try
                    {
                        var tz = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                        now = TimeZoneInfo.ConvertTime(now, tz);
                    }
                    catch
                    {
                        // Use local time if timezone conversion fails
                    }

                    // Process last trade with timing
                    if (tickData.LastTradePrice > 0 && volumeDelta > 0)
                    {
                        double lastPrice = instrument.MasterInstrument.RoundToTickSize(tickData.LastTradePrice);

                        var callbackTimer = Stopwatch.StartNew();
                        callback(MarketDataType.Last, lastPrice, volumeDelta, now, 0L);
                        callbackTimer.Stop();

                        TrackCallbackTiming(callbackTimer.ElapsedMilliseconds);

                        // Process bid/ask only when there's a trade
                        if (tickData.BuyPrice > 0)
                        {
                            double bidPrice = instrument.MasterInstrument.RoundToTickSize(tickData.BuyPrice);
                            callback(MarketDataType.Bid, bidPrice, tickData.BuyQty, now, 0L);
                        }

                        if (tickData.SellPrice > 0)
                        {
                            double askPrice = instrument.MasterInstrument.RoundToTickSize(tickData.SellPrice);
                            callback(MarketDataType.Ask, askPrice, tickData.SellQty, now, 0L);
                        }
                    }

                    // Process daily statistics
                    ProcessAdditionalMarketData(callback, instrument, tickData, now);

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
                double highPrice = instrument.MasterInstrument.RoundToTickSize(tickData.High);
                callback(MarketDataType.DailyHigh, highPrice, 0L, now, 0L);
            }

            if (tickData.Low > 0)
            {
                double lowPrice = instrument.MasterInstrument.RoundToTickSize(tickData.Low);
                callback(MarketDataType.DailyLow, lowPrice, 0L, now, 0L);
            }

            if (tickData.Open > 0)
            {
                double openPrice = instrument.MasterInstrument.RoundToTickSize(tickData.Open);
                callback(MarketDataType.Opening, openPrice, 0L, now, 0L);
            }

            if (tickData.Close > 0)
            {
                double closePrice = instrument.MasterInstrument.RoundToTickSize(tickData.Close);
                callback(MarketDataType.LastClose, closePrice, 0L, now, 0L);
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
            sb.AppendLine($"Processing Task Status: {_processingTask?.Status}");
            sb.AppendLine($"Ticks Queued: {Interlocked.Read(ref _ticksQueued)}");
            sb.AppendLine($"Ticks Processed: {Interlocked.Read(ref _ticksProcessed)}");
            sb.AppendLine($"Pending Ticks: {Interlocked.Read(ref _ticksQueued) - Interlocked.Read(ref _ticksProcessed)}");
            sb.AppendLine($"Callbacks Executed: {Interlocked.Read(ref _callbacksExecuted)}");
            sb.AppendLine($"Callback Errors: {Interlocked.Read(ref _callbackErrors)}");
            sb.AppendLine($"Pool Hits: {Interlocked.Read(ref _poolHits)}");
            sb.AppendLine($"Pool Misses: {Interlocked.Read(ref _poolMisses)}");
            var totalPool = Interlocked.Read(ref _poolHits) + Interlocked.Read(ref _poolMisses);
            sb.AppendLine($"Pool Hit Rate: {(totalPool > 0 ? (Interlocked.Read(ref _poolHits) * 100.0 / totalPool) : 0.0):F1}%");
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

                // Pool efficiency
                var totalPoolRequests = Interlocked.Read(ref _poolHits) + Interlocked.Read(ref _poolMisses);
                var poolHitRate = totalPoolRequests > 0 ?
                    (Interlocked.Read(ref _poolHits) * 100.0 / totalPoolRequests) : 0.0;

                // Determine overall health status
                var healthStatus = AssessHealthStatus(queueDepth, callbackSuccessRate, processingEfficiency, gc2Rate);

                Log(
                    $"HEALTH REPORT {healthStatus}\n" +
                    $"   Queue: {queueDepth} pending | {queuedRate}/30s in, {processedRate}/30s out | {processingEfficiency:F1}% efficiency\n" +
                    $"   Callbacks: {callbacksRate}/30s | {callbackSuccessRate:F1}% success | {avgCallbackTime:F3}ms avg | {slowCallbackPercentage:F1}% slow (>1ms)\n" +
                    $"   Backpressure: {_currentBackpressureState} | {ticksDroppedBP} dropped | {oldestDropped} aged out | Events: W{warningEvents}/C{criticalEvents}\n" +
                    $"   Memory: {totalMemoryMB}MB | GC: {gc0Rate}/0, {gc1Rate}/1, {gc2Rate}/2 | Pool: {poolHitRate:F1}% hit rate\n" +
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

        private void Log(string message)
        {
            // Disabled - too verbose for NinjaTrader control panel
            // try
            // {
            //     NinjaTrader.NinjaScript.NinjaScript.Log($"[OTP] {message}", NinjaTrader.Cbi.LogLevel.Information);
            // }
            // catch
            // {
            //     // Ignore logging errors
            // }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _cancellationTokenSource.Cancel();

            try
            {
                // Stop health monitoring timer
                _healthMonitoringTimer?.Stop();
                _healthMonitoringTimer?.Dispose();

                _processingTask?.Wait(5000); // Wait up to 5 seconds for graceful shutdown
            }
            catch (Exception ex)
            {
                Log($"Error during OptimizedTickProcessor disposal: {ex.Message}");
            }

            // Dispose performance monitor
            _performanceMonitor?.Dispose();

            // Dispose resources
            _queueSemaphore?.Dispose();
            _cancellationTokenSource.Dispose();
            Log("OptimizedTickProcessor disposed");
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
