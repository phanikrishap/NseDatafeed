using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// High-performance async synthetic straddle processor
    /// Replaces synchronous processing with BlockingCollection producer-consumer pattern
    /// </summary>
    public class AsyncStraddleProcessor : IDisposable
    {
        // BlockingCollection-based processing pipeline
        private readonly BlockingCollection<StraddleTickRequest> _inputQueue;
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
        private DateTime _startTime = DateTime.UtcNow;

        /// <summary>
        /// Event fired when a straddle price is calculated (for UI updates)
        /// Parameters: straddleSymbol, price, cePrice, pePrice
        /// </summary>
        public event Action<string, double, double, double> StraddlePriceCalculated;
        
        // Configuration
        private const int QUEUE_CAPACITY = 10000;
        private const int PROCESSING_WORKERS = 2; // Dedicated workers for parallel processing
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
            
            // Create bounded queues with backpressure handling
            _inputQueue = new BlockingCollection<StraddleTickRequest>(QUEUE_CAPACITY);
            _outputQueue = new BlockingCollection<StraddleResult>(QUEUE_CAPACITY);
            
            // Create cancellation token
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Start processing tasks
            _processingTasks = new Task[PROCESSING_WORKERS];
            for (int i = 0; i < PROCESSING_WORKERS; i++)
            {
                int workerId = i;
                _processingTasks[i] = Task.Run(() => ProcessStraddleTicks(workerId, _cancellationTokenSource.Token));
            }
            
            // Start publishing task
            _publishingTask = Task.Run(() => PublishStraddleResults(_cancellationTokenSource.Token));
            
            Logger.Info($"[AsyncStraddleProcessor] Started with {PROCESSING_WORKERS} workers, {QUEUE_CAPACITY} capacity");
        }
        
        /// <summary>
        /// Queue a tick for async straddle processing
        /// </summary>
        public bool QueueTickForProcessing(string instrumentSymbol, Tick tick)
        {
            if (_isDisposed || string.IsNullOrEmpty(instrumentSymbol) || tick == null)
                return false;
            
            try
            {
                var request = new StraddleTickRequest
                {
                    InstrumentSymbol = instrumentSymbol,
                    Tick = tick,
                    QueueTime = DateTime.UtcNow
                };
                
                // Non-blocking try add
                bool queued = _inputQueue.TryAdd(request);
                
                if (queued)
                {
                    Interlocked.Increment(ref _ticksReceived);
                }
                
                return queued;
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
        /// processing worker for straddle tick processing
        /// </summary>
        private void ProcessStraddleTicks(int workerId, CancellationToken cancellationToken)
        {
            try
            {
                var batchBuffer = new List<StraddleTickRequest>(MAX_BATCH_SIZE);
                
                foreach (var request in _inputQueue.GetConsumingEnumerable(cancellationToken))
                {
                    batchBuffer.Add(request);
                    
                    // Process immediately if batch is full or after a small timeout
                    // Note: Simplified batching for BlockingCollection
                    if (batchBuffer.Count >= MAX_BATCH_SIZE || _inputQueue.Count == 0)
                    {
                        ProcessBatch(workerId, batchBuffer);
                        batchBuffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Worker {workerId} error: {ex.Message}");
                Interlocked.Increment(ref _processingErrors);
            }
        }
        
        /// <summary>
        /// Process a batch of straddle tick requests
        /// </summary>
        private void ProcessBatch(int workerId, List<StraddleTickRequest> batch)
        {
            foreach (var request in batch)
            {
                ProcessSingleStraddle(request);
            }
            Interlocked.Add(ref _straddlesProcessed, batch.Count);
        }
        
        /// <summary>
        /// Process a single straddle tick and generate results
        /// </summary>
        private void ProcessSingleStraddle(StraddleTickRequest request)
        {
            try
            {
                // Find affected straddles
                if (!_legToStraddleMapping.TryGetValue(request.InstrumentSymbol, out var straddleSymbols))
                    return;
                
                foreach (var straddleSymbol in straddleSymbols)
                {
                    if (!_straddleStates.TryGetValue(straddleSymbol, out var state))
                        continue;
                    
                    // Update straddle state with new tick
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
                            ProcessingLatency = (DateTime.UtcNow - request.QueueTime).TotalMilliseconds
                        };
                        
                        // Try to queue result
                        _outputQueue.TryAdd(result);

                        // Fire event for UI updates (Option Chain window)
                        StraddlePriceCalculated?.Invoke(straddleSymbol, straddlePrice, state.LastCEPrice, state.LastPEPrice);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[AsyncStraddleProcessor] Error processing straddle: {ex.Message}");
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
            
            return new AsyncStraddleMetrics
            {
                TicksReceived = _ticksReceived,
                StraddlesProcessed = _straddlesProcessed,
                StraddlesPublished = _straddlesPublished,
                ProcessingErrors = _processingErrors,
                UptimeSeconds = uptime,
                TicksPerSecond = uptime > 0 ? _ticksReceived / uptime : 0,
                ProcessingWorkers = PROCESSING_WORKERS,
                QueueCapacity = QUEUE_CAPACITY
            };
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            try
            {
                _cancellationTokenSource.Cancel();
                _inputQueue.CompleteAdding();
                _outputQueue.CompleteAdding();
                
                Task.WaitAll(new List<Task>(_processingTasks) { _publishingTask }.ToArray(), TimeSpan.FromSeconds(5));
                
                _cancellationTokenSource.Dispose();
                _inputQueue.Dispose();
                _outputQueue.Dispose();
                
                Logger.Info("[AsyncStraddleProcessor] Disposed successfully");
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
    }
    
    public class AsyncStraddleMetrics
    {
        public long TicksReceived { get; set; }
        public long StraddlesProcessed { get; set; }
        public long StraddlesPublished { get; set; }
        public long ProcessingErrors { get; set; }
        public double UptimeSeconds { get; set; }
        public double TicksPerSecond { get; set; }
        public int ProcessingWorkers { get; set; }
        public int QueueCapacity { get; set; }
    }
}
