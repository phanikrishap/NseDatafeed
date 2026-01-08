using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// High-performance tick processor (Facade).
    /// Orchestrates sharded processing, caching, and health monitoring.
    /// </summary>
    public class OptimizedTickProcessor : IDisposable
    {
        private const int SHARD_COUNT = 4;
        private const int QUEUE_CAPACITY = 16384;

        private readonly Shard[] _shards;
        private readonly ShardProcessor[] _shardProcessors;
        private TickCacheManager _cacheManager;
        private readonly TickSubscriptionRegistry _subscriptionRegistry;
        private readonly TickProcessorHealthMonitor _healthMonitor;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly BackpressureManager _backpressureManager;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, string> _symbolMappingCache = new ConcurrentDictionary<string, string>();
        private readonly Subject<TickStreamItem> _tickSubject = new Subject<TickStreamItem>();

        private long _ticksQueued = 0;
        private bool _isDisposed = false;
        private bool _isUnderMemoryPressure = false;

        public event Action<string, double> OptionTickReceived;
        public IObservable<TickStreamItem> TickStream => _tickSubject.AsObservable();
        public IObservable<TickStreamItem> OptionTickStream => _tickSubject.Where(t => t.IsOption).AsObservable();

        public OptimizedTickProcessor(int maxQueueSize = 20000)
        {
            _performanceMonitor = new PerformanceMonitor(TimeSpan.FromSeconds(30));
            _backpressureManager = new BackpressureManager(maxQueueSize);
            _cacheManager = new TickCacheManager();
            _subscriptionRegistry = new TickSubscriptionRegistry();

            _shards = new Shard[SHARD_COUNT];
            _shardProcessors = new ShardProcessor[SHARD_COUNT];

            for (int i = 0; i < SHARD_COUNT; i++)
            {
                _shards[i] = new Shard(QUEUE_CAPACITY);
                _shardProcessors[i] = new ShardProcessor(i, _shards[i], _cacheManager, _subscriptionRegistry, _performanceMonitor, _tickSubject, (s, p) => OptionTickReceived?.Invoke(s, p));
                
                int shardIndex = i;
                _shards[i].WorkerTask = Task.Factory.StartNew(
                    () => _shardProcessors[shardIndex].ProcessLoop(_cts.Token),
                    _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            _healthMonitor = new TickProcessorHealthMonitor((int)(maxQueueSize * 0.6), (int)(maxQueueSize * 0.8), GetHealthSnapshot);
            _healthMonitor.HealthAlert += (msg, metrics) => Logger.Warn($"[OTP-HEALTH] {msg}");
            _healthMonitor.Start();

            Logger.Info($"[OTP] Initialized with {SHARD_COUNT} shards and modular components.");
        }

        public bool QueueTick(string nativeSymbolName, ZerodhaTickData tickData)
        {
            if (_isDisposed) return false;

            // Optional: Map symbol if generic name is passed
            if (_symbolMappingCache.TryGetValue(nativeSymbolName, out string mapped)) nativeSymbolName = mapped;

            int shardIndex = (nativeSymbolName.GetHashCode() & 0x7FFFFFFF) % SHARD_COUNT;
            var shard = _shards[shardIndex];

            if (shard.Queue.Count >= QUEUE_CAPACITY - 1000)
            {
                var bp = _backpressureManager.EvaluateTick(shard.Queue.Count, nativeSymbolName);
                if (!bp.shouldQueue) { _performanceMonitor.RecordTickDropped(nativeSymbolName); return false; }
            }

            var item = new TickProcessingItem { NativeSymbolName = nativeSymbolName, TickData = tickData, QueueTime = DateTime.UtcNow };
            if (shard.Queue.TryAdd(item))
            {
                Interlocked.Increment(ref _ticksQueued);
                _performanceMonitor.RecordTickReceived(nativeSymbolName);
                return true;
            }

            _performanceMonitor.RecordTickDropped(nativeSymbolName);
            return false;
        }

        public void UpdateSubscriptionCache(ConcurrentDictionary<string, L1Subscription> subscriptions)
        {
            CheckMemoryPressure();
            var result = _subscriptionRegistry.UpdateSubscriptions(subscriptions, _isUnderMemoryPressure);
            _cacheManager.ReplayForNewCallbacks(result.callbacks);
        }

        public void UpdateL2SubscriptionCache(ConcurrentDictionary<string, L2Subscription> subscriptions) 
            => _subscriptionRegistry.UpdateL2Subscriptions(subscriptions);

        /// <summary>
        /// Adds or updates symbol mappings. Mappings allow ticks received for one symbol (Value)
        /// to be forwarded to subscribers of another symbol (Key).
        /// Example: { "NIFTY_I": "NIFTY26JANFUT" } means ticks for NIFTY26JANFUT go to NIFTY_I subscribers.
        /// </summary>
        public void UpdateSymbolMappingCache(Dictionary<string, string> mappings)
        {
            // Add/update mappings without clearing existing ones
            // The cache stores: receivedSymbol -> subscriberSymbol (reversed from input)
            foreach (var kvp in mappings)
            {
                _symbolMappingCache[kvp.Value] = kvp.Key;
            }
        }

        private HealthMetricsSnapshot GetHealthSnapshot()
        {
            var bp = _backpressureManager.GetMetrics();
            return new HealthMetricsSnapshot
            {
                TicksQueued = Interlocked.Read(ref _ticksQueued),
                TicksProcessed = _shardProcessors.Sum(p => p.TicksProcessed),
                CallbacksExecuted = _shardProcessors.Sum(p => p.CallbacksExecuted),
                CallbackErrors = _shardProcessors.Sum(p => p.CallbackErrors),
                BackpressureState = bp.CurrentState
            };
        }

        private void CheckMemoryPressure()
        {
            long memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
            _isUnderMemoryPressure = memoryMB > 450;
            if (_isUnderMemoryPressure) GC.Collect(1, GCCollectionMode.Optimized);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts.Cancel();
            _healthMonitor.Dispose();
            _performanceMonitor.Dispose();
            foreach (var shard in _shards) { shard.Queue.CompleteAdding(); shard.Queue.Dispose(); }
            _tickSubject.OnCompleted();
            _tickSubject.Dispose();
            _cts.Dispose();
        }

        // Diagnostic helpers
        public TickProcessorMetrics GetMetrics()
        {
            var perf = _performanceMonitor.GetMetrics();
            return new TickProcessorMetrics
            {
                TicksQueued = Interlocked.Read(ref _ticksQueued),
                TicksProcessed = _shardProcessors.Sum(p => p.TicksProcessed),
                AverageProcessingTimeMs = perf.AverageProcessingTimeMs,
                CurrentTicksPerSecond = perf.CurrentTicksPerSecond
            };
        }

        public string GetDiagnosticInfo()
        {
            var perf = _performanceMonitor.GetMetrics();
            return $"Ticks Queued: {_ticksQueued}, Avg Processing: {perf.AverageProcessingTimeMs:F2}ms, TPS: {perf.CurrentTicksPerSecond:F0}";
        }

        public void ClearReplayedSymbolsTracking() => _cacheManager = new TickCacheManager(); // Simple way to clear if needed, or add method to manager
    }
}
