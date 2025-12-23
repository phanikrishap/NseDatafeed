using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace QANinjaAdapter.Services.MarketData
{
    /// <summary>
    /// Performance monitoring utility for tracking tick processing performance
    /// Helps identify bottlenecks and monitor system health
    /// </summary>
    public class PerformanceMonitor : IDisposable
    {
        private readonly Timer _reportingTimer;
        private readonly ConcurrentDictionary<string, PerformanceCounter> _counters = new ConcurrentDictionary<string, PerformanceCounter>();
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private volatile bool _isDisposed = false;

        // Limit counter dictionary size to prevent memory growth
        private const int MAX_COUNTERS = 500;

        // Performance metrics
        private long _totalTicksReceived = 0;
        private long _totalTicksProcessed = 0;
        private long _totalTicksDropped = 0;
        private long _totalProcessingTimeMs = 0;
        private long _peakTicksPerSecond = 0;
        private long _currentTicksPerSecond = 0;
        private DateTime _lastReportTime = DateTime.UtcNow;
        private long _lastTickCount = 0;

        public PerformanceMonitor(TimeSpan reportingInterval = default)
        {
            if (reportingInterval == default)
                reportingInterval = TimeSpan.FromSeconds(30);

            _reportingTimer = new Timer(GeneratePerformanceReport, null, reportingInterval, reportingInterval);
            Log("PerformanceMonitor initialized for real-time tick processing metrics");
        }

        /// <summary>
        /// Records a tick being received
        /// </summary>
        public void RecordTickReceived(string symbol = null)
        {
            Interlocked.Increment(ref _totalTicksReceived);

            if (!string.IsNullOrEmpty(symbol) && _counters.Count < MAX_COUNTERS)
            {
                var counter = _counters.GetOrAdd(symbol, _ => new PerformanceCounter());
                Interlocked.Increment(ref counter.TicksReceived);
            }
        }

        /// <summary>
        /// Records a tick being processed with timing information
        /// </summary>
        public void RecordTickProcessed(string symbol, long processingTimeMs)
        {
            Interlocked.Increment(ref _totalTicksProcessed);
            Interlocked.Add(ref _totalProcessingTimeMs, processingTimeMs);

            if (!string.IsNullOrEmpty(symbol) && _counters.TryGetValue(symbol, out var counter))
            {
                Interlocked.Increment(ref counter.TicksProcessed);
                Interlocked.Add(ref counter.TotalProcessingTimeMs, processingTimeMs);

                // Update max processing time
                long currentMax = counter.MaxProcessingTimeMs;
                while (processingTimeMs > currentMax)
                {
                    long original = Interlocked.CompareExchange(ref counter.MaxProcessingTimeMs, processingTimeMs, currentMax);
                    if (original == currentMax) break;
                    currentMax = original;
                }
            }
        }

        /// <summary>
        /// Records a tick being dropped due to congestion
        /// </summary>
        public void RecordTickDropped(string symbol = null)
        {
            Interlocked.Increment(ref _totalTicksDropped);

            if (!string.IsNullOrEmpty(symbol) && _counters.TryGetValue(symbol, out var counter))
            {
                Interlocked.Increment(ref counter.TicksDropped);
            }
        }

        /// <summary>
        /// Records processing latency for a tick
        /// </summary>
        public void RecordProcessingLatency(string symbol, TimeSpan latency)
        {
            if (!string.IsNullOrEmpty(symbol) && _counters.TryGetValue(symbol, out var counter))
            {
                long latencyMs = (long)latency.TotalMilliseconds;

                // Update max latency
                long currentMax = counter.MaxLatencyMs;
                while (latencyMs > currentMax)
                {
                    long original = Interlocked.CompareExchange(ref counter.MaxLatencyMs, latencyMs, currentMax);
                    if (original == currentMax) break;
                    currentMax = original;
                }

                // Simple moving average for latency
                Interlocked.Add(ref counter.TotalLatencyMs, latencyMs);
                Interlocked.Increment(ref counter.LatencyMeasurements);
            }
        }

        /// <summary>
        /// Gets current performance metrics
        /// </summary>
        public PerformanceMetrics GetMetrics()
        {
            var now = DateTime.UtcNow;
            var timeSinceLastReport = (now - _lastReportTime).TotalSeconds;

            if (timeSinceLastReport > 0)
            {
                var currentTicks = Interlocked.Read(ref _totalTicksReceived);
                _currentTicksPerSecond = (long)((currentTicks - _lastTickCount) / timeSinceLastReport);

                // Update peak if current is higher
                long currentPeak = _peakTicksPerSecond;
                while (_currentTicksPerSecond > currentPeak)
                {
                    long original = Interlocked.CompareExchange(ref _peakTicksPerSecond, _currentTicksPerSecond, currentPeak);
                    if (original == currentPeak) break;
                    currentPeak = original;
                }

                _lastTickCount = currentTicks;
                _lastReportTime = now;
            }

            return new PerformanceMetrics
            {
                TotalTicksReceived = Interlocked.Read(ref _totalTicksReceived),
                TotalTicksProcessed = Interlocked.Read(ref _totalTicksProcessed),
                TotalTicksDropped = Interlocked.Read(ref _totalTicksDropped),
                CurrentTicksPerSecond = _currentTicksPerSecond,
                PeakTicksPerSecond = _peakTicksPerSecond,
                AverageProcessingTimeMs = _totalTicksProcessed > 0 ?
                    (double)_totalProcessingTimeMs / _totalTicksProcessed : 0,
                UptimeSeconds = _uptime.Elapsed.TotalSeconds,
                SymbolCount = _counters.Count,
                ProcessingEfficiency = _totalTicksReceived > 0 ?
                    (double)_totalTicksProcessed / _totalTicksReceived * 100 : 0
            };
        }

        /// <summary>
        /// Gets performance metrics for a specific symbol
        /// </summary>
        public SymbolPerformanceMetrics GetSymbolMetrics(string symbol)
        {
            if (_counters.TryGetValue(symbol, out var counter))
            {
                return new SymbolPerformanceMetrics
                {
                    Symbol = symbol,
                    TicksReceived = Interlocked.Read(ref counter.TicksReceived),
                    TicksProcessed = Interlocked.Read(ref counter.TicksProcessed),
                    TicksDropped = Interlocked.Read(ref counter.TicksDropped),
                    MaxProcessingTimeMs = Interlocked.Read(ref counter.MaxProcessingTimeMs),
                    AverageProcessingTimeMs = counter.TicksProcessed > 0 ?
                        (double)counter.TotalProcessingTimeMs / counter.TicksProcessed : 0,
                    MaxLatencyMs = Interlocked.Read(ref counter.MaxLatencyMs),
                    AverageLatencyMs = counter.LatencyMeasurements > 0 ?
                        (double)counter.TotalLatencyMs / counter.LatencyMeasurements : 0
                };
            }

            return null;
        }

        /// <summary>
        /// Generates and logs a performance report
        /// </summary>
        private void GeneratePerformanceReport(object state)
        {
            if (_isDisposed) return;

            try
            {
                var metrics = GetMetrics();

                Log($"=== PERFORMANCE REPORT ===");
                Log($"Uptime: {TimeSpan.FromSeconds(metrics.UptimeSeconds):hh\\:mm\\:ss}");
                Log($"Ticks Received: {metrics.TotalTicksReceived:N0}");
                Log($"Ticks Processed: {metrics.TotalTicksProcessed:N0}");
                Log($"Ticks Dropped: {metrics.TotalTicksDropped:N0}");
                Log($"Current Rate: {metrics.CurrentTicksPerSecond:N0} ticks/sec");
                Log($"Peak Rate: {metrics.PeakTicksPerSecond:N0} ticks/sec");
                Log($"Avg Processing Time: {metrics.AverageProcessingTimeMs:F2} ms");
                Log($"Processing Efficiency: {metrics.ProcessingEfficiency:F1}%");
                Log($"Active Symbols: {metrics.SymbolCount:N0}");

                // Report on worst performing symbols
                var worstSymbols = GetWorstPerformingSymbols(5);
                if (worstSymbols.Count > 0)
                {
                    Log($"Symbols with highest latency:");
                    foreach (var symbolMetrics in worstSymbols)
                    {
                        Log($"  {symbolMetrics.Symbol}: {symbolMetrics.AverageLatencyMs:F1}ms avg, " +
                            $"{symbolMetrics.MaxLatencyMs}ms max, {symbolMetrics.TicksDropped} dropped");
                    }
                }

                Log($"=== END PERFORMANCE REPORT ===");
            }
            catch (Exception ex)
            {
                Log($"Error generating performance report: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the worst performing symbols by average latency
        /// </summary>
        private List<SymbolPerformanceMetrics> GetWorstPerformingSymbols(int count)
        {
            var symbolMetrics = new List<SymbolPerformanceMetrics>();

            foreach (var kvp in _counters)
            {
                var metrics = GetSymbolMetrics(kvp.Key);
                if (metrics != null && metrics.TicksProcessed > 10) // Only consider symbols with meaningful data
                {
                    symbolMetrics.Add(metrics);
                }
            }

            symbolMetrics.Sort((a, b) => b.AverageLatencyMs.CompareTo(a.AverageLatencyMs));

            if (symbolMetrics.Count > count)
            {
                symbolMetrics.RemoveRange(count, symbolMetrics.Count - count);
            }

            return symbolMetrics;
        }

        /// <summary>
        /// Resets all performance counters
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _totalTicksReceived, 0);
            Interlocked.Exchange(ref _totalTicksProcessed, 0);
            Interlocked.Exchange(ref _totalTicksDropped, 0);
            Interlocked.Exchange(ref _totalProcessingTimeMs, 0);
            Interlocked.Exchange(ref _peakTicksPerSecond, 0);
            Interlocked.Exchange(ref _currentTicksPerSecond, 0);

            _counters.Clear();
            _uptime.Restart();
            _lastReportTime = DateTime.UtcNow;
            _lastTickCount = 0;

            Log("Performance counters reset");
        }

        private void Log(string message)
        {
            try
            {
                // Log to file only, not to NinjaTrader control panel (too verbose)
                Logger.Info($"[PERF] {message}");
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
            _reportingTimer?.Dispose();
            _uptime?.Stop();

            Log("PerformanceMonitor disposed");
        }
    }

    /// <summary>
    /// Performance counter for individual symbols
    /// </summary>
    internal class PerformanceCounter
    {
        public long TicksReceived = 0;
        public long TicksProcessed = 0;
        public long TicksDropped = 0;
        public long TotalProcessingTimeMs = 0;
        public long MaxProcessingTimeMs = 0;
        public long TotalLatencyMs = 0;
        public long MaxLatencyMs = 0;
        public long LatencyMeasurements = 0;
    }

    /// <summary>
    /// Overall performance metrics
    /// </summary>
    public class PerformanceMetrics
    {
        public long TotalTicksReceived { get; set; }
        public long TotalTicksProcessed { get; set; }
        public long TotalTicksDropped { get; set; }
        public long CurrentTicksPerSecond { get; set; }
        public long PeakTicksPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double UptimeSeconds { get; set; }
        public int SymbolCount { get; set; }
        public double ProcessingEfficiency { get; set; }
    }

    /// <summary>
    /// Performance metrics for a specific symbol
    /// </summary>
    public class SymbolPerformanceMetrics
    {
        public string Symbol { get; set; }
        public long TicksReceived { get; set; }
        public long TicksProcessed { get; set; }
        public long TicksDropped { get; set; }
        public long MaxProcessingTimeMs { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public long MaxLatencyMs { get; set; }
        public double AverageLatencyMs { get; set; }
    }
}
