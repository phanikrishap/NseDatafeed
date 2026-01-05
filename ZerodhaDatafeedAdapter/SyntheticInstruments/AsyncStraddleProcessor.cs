using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// High-performance async synthetic straddle processor with sharded architecture.
    ///
    /// Architecture: Sharded Processing (Thread-Safe by Design)
    /// ═══════════════════════════════════════════════════════════════════════════
    ///
    /// Problem Solved: Race condition where multiple workers could update the same
    /// StraddleState concurrently when CE and PE ticks arrived simultaneously.
    ///
    /// Solution: Partition ticks by straddle symbol hash into N shards. Each shard
    /// has its own BlockingCollection and dedicated worker. All ticks for a given
    /// straddle ALWAYS go to the same shard, guaranteeing single-threaded access
    /// to StraddleState without locks.
    ///
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │                    Sharded Straddle Processing                           │
    /// ├─────────────────────────────────────────────────────────────────────────┤
    /// │                                                                          │
    /// │  Input Tick ──► Route by Symbol Hash ──► Shard N ──► Worker N           │
    /// │                                                                          │
    /// │  ┌─────────────────┐                                                     │
    /// │  │ QueueTick(CE)   │ ──► Hash("NIFTY26JAN26000_STRDL") % 4 = 2          │
    /// │  └─────────────────┘              │                                      │
    /// │                                   ▼                                      │
    /// │  ┌─────────────────┐     ┌──────────────────┐     ┌──────────────────┐  │
    /// │  │ QueueTick(PE)   │ ──► │ _shardQueues[2]  │ ──► │ Worker 2 ONLY    │  │
    /// │  └─────────────────┘     │ (BlockingColl)   │     │ updates this     │  │
    /// │                          └──────────────────┘     │ StraddleState    │  │
    /// │                                                   └──────────────────┘  │
    /// │                                                                          │
    /// │  Benefits:                                                               │
    /// │  - No locks needed: Thread affinity guarantees single-writer access     │
    /// │  - High throughput: Parallel processing across different straddles      │
    /// │  - Deterministic: Same symbol always routes to same worker              │
    /// └─────────────────────────────────────────────────────────────────────────┘
    /// </summary>
    public class AsyncStraddleProcessor : IDisposable
    {
        // ═══════════════════════════════════════════════════════════════════
        // SHARDED PROCESSING ARCHITECTURE
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sharded input queues - each shard has its own BlockingCollection.
        /// Ticks are routed to shards based on straddle symbol hash.
        /// </summary>
        private readonly BlockingCollection<StraddleTickRequest>[] _shardQueues;

        /// <summary>
        /// Output queue for publishing results (single queue, thread-safe).
        /// </summary>
        private readonly BlockingCollection<StraddleResult> _outputQueue;

        // Processing tasks and cancellation
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task[] _processingTasks;
        private readonly Task _publishingTask;

        // Straddle state management
        private readonly ConcurrentDictionary<string, StraddleState> _straddleStates;
        private readonly ConcurrentDictionary<string, List<string>> _legToStraddleMapping;

        // Performance metrics
        private long _ticksReceived = 0;
        private long _straddlesProcessed = 0;
        private long _straddlesPublished = 0;
        private long _processingErrors = 0;
        private readonly long[] _shardTickCounts; // Per-shard tick counts
        private DateTime _startTime = DateTime.UtcNow;

        /// <summary>
        /// Event fired when a straddle price is calculated (for UI updates)
        /// Parameters: straddleSymbol, price, cePrice, pePrice
        /// </summary>
        public event Action<string, double, double, double> StraddlePriceCalculated;

        // Configuration
        private const int QUEUE_CAPACITY_PER_SHARD = 2500; // 10000 / 4 shards
        private const int NUM_SHARDS = 4; // Number of processing shards
        private const int MAX_BATCH_SIZE = 100;
        private const int BATCH_TIMEOUT_MS = 5; // 5ms max delay for low-latency

        private readonly ZerodhaAdapter _adapter;
        private volatile bool _isDisposed = false;

        public AsyncStraddleProcessor(ZerodhaAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

            // Initialize state management
            _straddleStates = new ConcurrentDictionary<string, StraddleState>();
            _legToStraddleMapping = new ConcurrentDictionary<string, List<string>>();

            // Create sharded input queues
            _shardQueues = new BlockingCollection<StraddleTickRequest>[NUM_SHARDS];
            _shardTickCounts = new long[NUM_SHARDS];
            for (int i = 0; i < NUM_SHARDS; i++)
            {
                _shardQueues[i] = new BlockingCollection<StraddleTickRequest>(QUEUE_CAPACITY_PER_SHARD);
            }

            // Create output queue
            _outputQueue = new BlockingCollection<StraddleResult>(QUEUE_CAPACITY_PER_SHARD * NUM_SHARDS);

            // Create cancellation token
            _cancellationTokenSource = new CancellationTokenSource();

            // Start one processing task per shard (thread affinity)
            _processingTasks = new Task[NUM_SHARDS];
            for (int i = 0; i < NUM_SHARDS; i++)
            {
                int shardId = i;
                _processingTasks[i] = Task.Run(() => ProcessShardTicks(shardId, _cancellationTokenSource.Token));
            }

            // Start publishing task
            _publishingTask = Task.Run(() => PublishStraddleResults(_cancellationTokenSource.Token));

            Logger.Info($"[AsyncStraddleProcessor] Started with {NUM_SHARDS} shards (thread-safe by design), {QUEUE_CAPACITY_PER_SHARD} capacity/shard");
        }

        /// <summary>
        /// Computes the shard index for a given straddle symbol.
        /// Uses consistent hashing to ensure same symbol always routes to same shard.
        /// </summary>
        private int GetShardIndex(string straddleSymbol)
        {
            if (string.IsNullOrEmpty(straddleSymbol)) return 0;

            // Use stable hash code (not GetHashCode which can vary across runs)
            int hash = 0;
            foreach (char c in straddleSymbol)
            {
                hash = (hash * 31) + c;
            }

            // Ensure positive and map to shard
            return (hash & 0x7FFFFFFF) % NUM_SHARDS;
        }
        
        /// <summary>
        /// Queue a tick for async straddle processing.
        /// Routes the tick to the appropriate shard based on straddle symbol hash.
        /// </summary>
        public bool QueueTickForProcessing(string instrumentSymbol, Tick tick)
        {
            if (_isDisposed || string.IsNullOrEmpty(instrumentSymbol) || tick == null)
                return false;

            try
            {
                // Find which straddles this instrument affects
                if (!_legToStraddleMapping.TryGetValue(instrumentSymbol, out var straddleSymbols))
                    return false;

                bool anyQueued = false;

                foreach (var straddleSymbol in straddleSymbols)
                {
                    var request = new StraddleTickRequest
                    {
                        InstrumentSymbol = instrumentSymbol,
                        StraddleSymbol = straddleSymbol, // Include straddle symbol for routing
                        Tick = tick,
                        QueueTime = DateTime.UtcNow
                    };

                    // Route to the correct shard based on straddle symbol
                    int shardIndex = GetShardIndex(straddleSymbol);

                    // Non-blocking try add to the shard's queue
                    bool queued = _shardQueues[shardIndex].TryAdd(request);

                    if (queued)
                    {
                        Interlocked.Increment(ref _ticksReceived);
                        Interlocked.Increment(ref _shardTickCounts[shardIndex]);
                        anyQueued = true;
                    }
                }

                return anyQueued;
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Error queueing tick: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Load straddle definitions and initialize state management
        /// </summary>
        public void LoadStraddleConfigurations(IEnumerable<StraddleDefinition> definitions)
        {
            if (definitions == null) return;

            foreach (var definition in definitions)
            {
                var state = new StraddleState(definition);
                _straddleStates.TryAdd(definition.SyntheticSymbolNinjaTrader, state);

                // Map CE symbol to straddles
                _legToStraddleMapping.AddOrUpdate(
                    definition.CESymbol,
                    new List<string> { definition.SyntheticSymbolNinjaTrader },
                    (key, existing) => { existing.Add(definition.SyntheticSymbolNinjaTrader); return existing; }
                );

                // Map PE symbol to straddles
                _legToStraddleMapping.AddOrUpdate(
                    definition.PESymbol,
                    new List<string> { definition.SyntheticSymbolNinjaTrader },
                    (key, existing) => { existing.Add(definition.SyntheticSymbolNinjaTrader); return existing; }
                );

                Logger.Info($"[AsyncStraddleProcessor] Loaded straddle: {definition.SyntheticSymbolNinjaTrader}");
            }
        }

        /// <summary>
        /// Clears existing straddle configurations and reloads with new definitions.
        /// Used when straddles_config.json is regenerated at runtime.
        /// </summary>
        public void ReloadConfigurations(IEnumerable<StraddleDefinition> definitions)
        {
            if (definitions == null) return;

            // Clear existing mappings
            _straddleStates.Clear();
            _legToStraddleMapping.Clear();

            Logger.Info("[AsyncStraddleProcessor] Cleared existing straddle configurations for reload");

            // Reload with new definitions
            LoadStraddleConfigurations(definitions);

            Logger.Info($"[AsyncStraddleProcessor] Reloaded with {_straddleStates.Count} straddle definitions");
        }
        
        /// <summary>
        /// Check if an instrument is a leg of any straddle
        /// </summary>
        public bool IsLegInstrument(string instrumentSymbol)
        {
            return _legToStraddleMapping.ContainsKey(instrumentSymbol);
        }
        
        /// <summary>
        /// Shard-specific processing worker.
        /// Each shard has exactly ONE worker, guaranteeing thread affinity for StraddleState access.
        /// This eliminates the race condition where multiple workers could update the same state.
        /// </summary>
        private void ProcessShardTicks(int shardId, CancellationToken cancellationToken)
        {
            try
            {
                Logger.Debug($"[AsyncStraddleProcessor] Shard {shardId} worker started");
                var batchBuffer = new List<StraddleTickRequest>(MAX_BATCH_SIZE);

                // Each shard only consumes from its own queue
                foreach (var request in _shardQueues[shardId].GetConsumingEnumerable(cancellationToken))
                {
                    batchBuffer.Add(request);

                    // Process immediately if batch is full or queue is empty
                    if (batchBuffer.Count >= MAX_BATCH_SIZE || _shardQueues[shardId].Count == 0)
                    {
                        ProcessBatch(shardId, batchBuffer);
                        batchBuffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                Logger.Debug($"[AsyncStraddleProcessor] Shard {shardId} worker stopped (cancelled)");
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Shard {shardId} worker error: {ex.Message}");
                Interlocked.Increment(ref _processingErrors);
            }
        }
        
        /// <summary>
        /// Process a batch of straddle tick requests.
        /// Called by a single shard worker, so no synchronization needed for state access.
        /// </summary>
        private void ProcessBatch(int shardId, List<StraddleTickRequest> batch)
        {
            foreach (var request in batch)
            {
                ProcessSingleStraddle(request, shardId);
            }
            Interlocked.Add(ref _straddlesProcessed, batch.Count);
        }
        
        /// <summary>
        /// Process a single straddle tick and generate results.
        /// Thread-safe by design: each shard worker only processes its assigned straddles.
        /// No locking needed because shardId guarantees single-threaded access to this StraddleState.
        /// </summary>
        private void ProcessSingleStraddle(StraddleTickRequest request, int shardId)
        {
            try
            {
                // The straddle symbol is now included in the request (set during routing)
                string straddleSymbol = request.StraddleSymbol;

                if (!_straddleStates.TryGetValue(straddleSymbol, out var state))
                    return;

                // Update straddle state with new tick
                // THREAD-SAFE: Only this shard's worker updates this state
                bool wasUpdated = UpdateStraddleState(state, request.InstrumentSymbol, request.Tick);

                if (wasUpdated && CanCalculateStraddlePrice(state))
                {
                    // Calculate straddle price
                    var straddlePrice = CalculateStraddlePrice(state);

                    // Queue result for publishing
                    var result = new StraddleResult
                    {
                        StraddleSymbol = straddleSymbol,
                        Price = straddlePrice,
                        Volume = Math.Max(state.LastCEVolume, state.LastPEVolume),
                        Timestamp = DateTime.UtcNow,
                        CEPrice = state.LastCEPrice,
                        PEPrice = state.LastPEPrice,
                        ProcessingLatency = (DateTime.UtcNow - request.QueueTime).TotalMilliseconds,
                        ShardId = shardId // Track which shard processed this
                    };

                    // Try to queue result (output queue is thread-safe)
                    _outputQueue.TryAdd(result);

                    // Fire event for UI updates (Option Chain window)
                    StraddlePriceCalculated?.Invoke(straddleSymbol, straddlePrice, state.LastCEPrice, state.LastPEPrice);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Shard {shardId} error processing straddle: {ex.Message}");
                Interlocked.Increment(ref _processingErrors);
            }
        }
        
        private bool UpdateStraddleState(StraddleState state, string instrumentSymbol, Tick tick)
        {
            bool wasUpdated = false;
            
            if (state.Definition.CESymbol.Equals(instrumentSymbol, StringComparison.OrdinalIgnoreCase))
            {
                state.LastCEPrice = tick.Price;
                state.LastCETimestamp = tick.Timestamp;
                state.LastCEVolume = tick.Volume;
                state.HasCEData = true;
                
                if (state.RecentCETicks.Count >= 5)
                {
                    state.RecentCETicks.RemoveAt(0);
                }
                state.RecentCETicks.Add(tick);
                
                wasUpdated = true;
            }
            else if (state.Definition.PESymbol.Equals(instrumentSymbol, StringComparison.OrdinalIgnoreCase))
            {
                state.LastPEPrice = tick.Price;
                state.LastPETimestamp = tick.Timestamp;
                state.LastPEVolume = tick.Volume;
                state.HasPEData = true;
                
                if (state.RecentPETicks.Count >= 5)
                {
                    state.RecentPETicks.RemoveAt(0);
                }
                state.RecentPETicks.Add(tick);
                
                wasUpdated = true;
            }
            
            return wasUpdated;
        }
        
        private bool CanCalculateStraddlePrice(StraddleState state)
        {
            return state.HasCEData && state.HasPEData && 
                   state.LastCEPrice > 0 && state.LastPEPrice > 0;
        }
        
        private double CalculateStraddlePrice(StraddleState state)
        {
            return state.LastCEPrice + state.LastPEPrice;
        }
        
        private void PublishStraddleResults(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var result in _outputQueue.GetConsumingEnumerable(cancellationToken))
                {
                    // Publish to NinjaTrader via adapter
                    _adapter.PublishSyntheticTickData(result.StraddleSymbol, result.Price, result.Timestamp, result.Volume, TickType.Last);
                    Interlocked.Increment(ref _straddlesPublished);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Publishing error: {ex.Message}");
            }
        }
        
        public AsyncStraddleMetrics GetMetrics()
        {
            var uptime = (DateTime.UtcNow - _startTime).TotalSeconds;

            // Collect per-shard queue depths
            var shardQueueDepths = new int[NUM_SHARDS];
            var shardTickCountsCopy = new long[NUM_SHARDS];
            for (int i = 0; i < NUM_SHARDS; i++)
            {
                shardQueueDepths[i] = _shardQueues[i].Count;
                shardTickCountsCopy[i] = Interlocked.Read(ref _shardTickCounts[i]);
            }

            return new AsyncStraddleMetrics
            {
                TicksReceived = _ticksReceived,
                StraddlesProcessed = _straddlesProcessed,
                StraddlesPublished = _straddlesPublished,
                ProcessingErrors = _processingErrors,
                UptimeSeconds = uptime,
                TicksPerSecond = uptime > 0 ? _ticksReceived / uptime : 0,
                NumShards = NUM_SHARDS,
                QueueCapacityPerShard = QUEUE_CAPACITY_PER_SHARD,
                ShardQueueDepths = shardQueueDepths,
                ShardTickCounts = shardTickCountsCopy
            };
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _cancellationTokenSource.Cancel();

                // Complete all shard queues
                for (int i = 0; i < NUM_SHARDS; i++)
                {
                    _shardQueues[i].CompleteAdding();
                }
                _outputQueue.CompleteAdding();

                // Wait for all processing tasks including the publishing task
                var allTasks = new List<Task>(_processingTasks) { _publishingTask };
                Task.WaitAll(allTasks.ToArray(), TimeSpan.FromSeconds(5));

                _cancellationTokenSource.Dispose();

                // Dispose all shard queues
                for (int i = 0; i < NUM_SHARDS; i++)
                {
                    _shardQueues[i].Dispose();
                }
                _outputQueue.Dispose();

                Logger.Info($"[AsyncStraddleProcessor] Disposed successfully ({NUM_SHARDS} shards)");
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Error during disposal: {ex.Message}");
            }
        }
    }
    
    public class StraddleTickRequest
    {
        public string InstrumentSymbol { get; set; }
        public string StraddleSymbol { get; set; } // Used for routing to correct shard
        public Tick Tick { get; set; }
        public DateTime QueueTime { get; set; }
    }
    
    public class StraddleResult
    {
        public string StraddleSymbol { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public double CEPrice { get; set; }
        public double PEPrice { get; set; }
        public double ProcessingLatency { get; set; }
        public int ShardId { get; set; } // Track which shard processed this result
    }
    
    public class AsyncStraddleMetrics
    {
        public long TicksReceived { get; set; }
        public long StraddlesProcessed { get; set; }
        public long StraddlesPublished { get; set; }
        public long ProcessingErrors { get; set; }
        public double UptimeSeconds { get; set; }
        public double TicksPerSecond { get; set; }

        // Sharding metrics
        public int NumShards { get; set; }
        public int QueueCapacityPerShard { get; set; }
        public int[] ShardQueueDepths { get; set; } // Current queue depth per shard
        public long[] ShardTickCounts { get; set; } // Total ticks processed per shard
    }
}
