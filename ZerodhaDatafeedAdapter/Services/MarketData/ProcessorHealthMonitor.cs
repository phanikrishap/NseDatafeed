using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using ZerodhaDatafeedAdapter.Models.MarketData;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Health monitoring for the tick processor.
    /// Tracks queue health, callback performance, memory pressure, and GC activity.
    /// Extracted from OptimizedTickProcessor for better separation of concerns.
    /// </summary>
    public class ProcessorHealthMonitor : IDisposable
    {
        #region Configuration

        private readonly int _warningQueueSize;
        private readonly int _criticalQueueSize;
        private readonly int _monitoringIntervalMs;

        #endregion

        #region State Tracking

        // Previous interval counts for rate calculations
        private long _lastQueuedCount = 0;
        private long _lastProcessedCount = 0;
        private long _lastCallbacksExecuted = 0;
        private long _lastCallbackErrors = 0;
        private int _lastGC0Count = 0;
        private int _lastGC1Count = 0;
        private int _lastGC2Count = 0;

        // Timer for periodic health checks
        private readonly System.Timers.Timer _healthTimer;
        private bool _isDisposed = false;

        // Callback to get current metrics from processor
        private readonly Func<HealthMetricsSnapshot> _metricsProvider;

        // Event for health alerts
        public event EventHandler<HealthAlertEventArgs> HealthAlertRaised;

        #endregion

        #region Constructor

        /// <summary>
        /// Initialize health monitor
        /// </summary>
        /// <param name="warningQueueSize">Queue size threshold for warnings</param>
        /// <param name="criticalQueueSize">Queue size threshold for critical alerts</param>
        /// <param name="metricsProvider">Function to get current metrics from processor</param>
        /// <param name="monitoringIntervalMs">Monitoring interval in milliseconds (default 30000)</param>
        public ProcessorHealthMonitor(
            int warningQueueSize,
            int criticalQueueSize,
            Func<HealthMetricsSnapshot> metricsProvider,
            int monitoringIntervalMs = 30000)
        {
            _warningQueueSize = warningQueueSize;
            _criticalQueueSize = criticalQueueSize;
            _metricsProvider = metricsProvider ?? throw new ArgumentNullException(nameof(metricsProvider));
            _monitoringIntervalMs = monitoringIntervalMs;

            _healthTimer = new System.Timers.Timer(monitoringIntervalMs);
            _healthTimer.Elapsed += OnHealthTimerElapsed;
            _healthTimer.AutoReset = true;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Start health monitoring
        /// </summary>
        public void Start()
        {
            _healthTimer.Start();
            Logger.Info("[HealthMonitor] Started");
        }

        /// <summary>
        /// Stop health monitoring
        /// </summary>
        public void Stop()
        {
            _healthTimer.Stop();
            Logger.Info("[HealthMonitor] Stopped");
        }

        /// <summary>
        /// Manually trigger a health check
        /// </summary>
        public HealthReport GetHealthReport()
        {
            var metrics = _metricsProvider();
            return GenerateHealthReport(metrics);
        }

        #endregion

        #region Health Check Logic

        private void OnHealthTimerElapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var metrics = _metricsProvider();
                LogHealthMetrics(metrics);
            }
            catch (Exception ex)
            {
                Logger.Error($"[HealthMonitor] Error in health check: {ex.Message}");
            }
        }

        private void LogHealthMetrics(HealthMetricsSnapshot metrics)
        {
            // Calculate rates (per monitoring interval)
            var queuedRate = metrics.TicksQueued - _lastQueuedCount;
            var processedRate = metrics.TicksProcessed - _lastProcessedCount;
            var callbacksRate = metrics.CallbacksExecuted - _lastCallbacksExecuted;
            var errorsRate = metrics.CallbackErrors - _lastCallbackErrors;

            // Queue health
            var queueDepth = metrics.TicksQueued - metrics.TicksProcessed;
            var processingEfficiency = queuedRate > 0 ? (processedRate * 100.0 / queuedRate) : 100.0;

            // Callback health
            var totalCallbacks = metrics.CallbacksExecuted + metrics.CallbackErrors;
            var callbackSuccessRate = totalCallbacks > 0 ? (metrics.CallbacksExecuted * 100.0 / totalCallbacks) : 100.0;

            // Memory metrics
            var currentGC0 = GC.CollectionCount(0);
            var currentGC1 = GC.CollectionCount(1);
            var currentGC2 = GC.CollectionCount(2);
            var gc0Rate = currentGC0 - _lastGC0Count;
            var gc1Rate = currentGC1 - _lastGC1Count;
            var gc2Rate = currentGC2 - _lastGC2Count;
            var totalMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;

            // Callback timing metrics
            var avgCallbackTime = metrics.CallbacksExecuted > 0 ?
                (double)metrics.TotalCallbackTimeMs / metrics.CallbacksExecuted : 0.0;
            var slowCallbackPercentage = metrics.CallbacksExecuted > 0 ?
                (metrics.SlowCallbacks * 100.0 / metrics.CallbacksExecuted) : 0.0;

            // Determine overall health status
            var healthStatus = AssessHealthStatus(queueDepth, callbackSuccessRate, processingEfficiency, gc2Rate, metrics.BackpressureState);

            // Log health report
            var intervalSeconds = _monitoringIntervalMs / 1000;
            Logger.Info(
                $"[HealthMonitor] {healthStatus}\n" +
                $"   Queue: {queueDepth} pending | {queuedRate}/{intervalSeconds}s in, {processedRate}/{intervalSeconds}s out | {processingEfficiency:F1}% efficiency\n" +
                $"   Callbacks: {callbacksRate}/{intervalSeconds}s | {callbackSuccessRate:F1}% success | {avgCallbackTime:F3}ms avg | {slowCallbackPercentage:F1}% slow (>1ms)\n" +
                $"   Backpressure: {metrics.BackpressureState} | {metrics.TicksDroppedBackpressure} dropped\n" +
                $"   Memory: {totalMemoryMB}MB | GC: {gc0Rate}/0, {gc1Rate}/1, {gc2Rate}/2\n" +
                $"   Totals: {metrics.TicksQueued:N0} queued, {metrics.TicksProcessed:N0} processed, {metrics.CallbacksExecuted:N0} callbacks");

            // Issue health alerts if needed
            var alerts = GenerateHealthAlerts(queueDepth, callbackSuccessRate, processingEfficiency, gc2Rate, totalMemoryMB, metrics.BackpressureState);
            foreach (var alert in alerts)
            {
                Logger.Warn($"[HealthMonitor] ALERT: {alert}");
                HealthAlertRaised?.Invoke(this, new HealthAlertEventArgs(alert));
            }

            // Update previous counts for next iteration
            _lastQueuedCount = metrics.TicksQueued;
            _lastProcessedCount = metrics.TicksProcessed;
            _lastCallbacksExecuted = metrics.CallbacksExecuted;
            _lastCallbackErrors = metrics.CallbackErrors;
            _lastGC0Count = currentGC0;
            _lastGC1Count = currentGC1;
            _lastGC2Count = currentGC2;
        }

        /// <summary>
        /// Assess overall health status based on key metrics
        /// </summary>
        public string AssessHealthStatus(long queueDepth, double callbackSuccessRate, double processingEfficiency, int gc2Rate, BackpressureState backpressureState)
        {
            // Critical issues
            if (backpressureState >= BackpressureState.Emergency || queueDepth > _criticalQueueSize ||
                callbackSuccessRate < 95.0 || processingEfficiency < 80.0 || gc2Rate > 2)
            {
                return "CRITICAL";
            }

            // Warning issues
            if (backpressureState >= BackpressureState.Warning || queueDepth > _warningQueueSize ||
                callbackSuccessRate < 98.0 || processingEfficiency < 95.0 || gc2Rate > 1)
            {
                return "WARNING";
            }

            // All good
            return "HEALTHY";
        }

        /// <summary>
        /// Generate specific health alerts for actionable issues
        /// </summary>
        private List<string> GenerateHealthAlerts(long queueDepth, double callbackSuccessRate, double processingEfficiency, int gc2Rate, long memoryMB, BackpressureState backpressureState)
        {
            var alerts = new List<string>();

            // Backpressure alerts
            if (backpressureState >= BackpressureState.Warning)
            {
                alerts.Add($"BACKPRESSURE {backpressureState}: Queue depth {queueDepth} exceeded threshold");
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
                alerts.Add($"HIGH GC PRESSURE: {gc2Rate} Gen2 collections in interval (target: <=1)");
            }

            if (memoryMB > 500)
            {
                alerts.Add($"HIGH MEMORY USAGE: {memoryMB}MB (consider monitoring)");
            }

            return alerts;
        }

        private HealthReport GenerateHealthReport(HealthMetricsSnapshot metrics)
        {
            var queueDepth = metrics.TicksQueued - metrics.TicksProcessed;
            var totalCallbacks = metrics.CallbacksExecuted + metrics.CallbackErrors;
            var callbackSuccessRate = totalCallbacks > 0 ? (metrics.CallbacksExecuted * 100.0 / totalCallbacks) : 100.0;
            var gc2Count = GC.CollectionCount(2);

            return new HealthReport
            {
                Status = AssessHealthStatus(queueDepth, callbackSuccessRate, 100.0, gc2Count - _lastGC2Count, metrics.BackpressureState),
                QueueDepth = queueDepth,
                CallbackSuccessRate = callbackSuccessRate,
                MemoryMB = GC.GetTotalMemory(false) / 1024 / 1024,
                BackpressureState = metrics.BackpressureState,
                Timestamp = DateTime.UtcNow
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _healthTimer?.Stop();
            _healthTimer?.Dispose();
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Snapshot of metrics from the tick processor for health monitoring
    /// </summary>
    public class HealthMetricsSnapshot
    {
        public long TicksQueued { get; set; }
        public long TicksProcessed { get; set; }
        public long CallbacksExecuted { get; set; }
        public long CallbackErrors { get; set; }
        public long TotalCallbackTimeMs { get; set; }
        public long SlowCallbacks { get; set; }
        public long VerySlowCallbacks { get; set; }
        public long TicksDroppedBackpressure { get; set; }
        public BackpressureState BackpressureState { get; set; }
    }

    /// <summary>
    /// Health report summary
    /// </summary>
    public class HealthReport
    {
        public string Status { get; set; }
        public long QueueDepth { get; set; }
        public double CallbackSuccessRate { get; set; }
        public long MemoryMB { get; set; }
        public BackpressureState BackpressureState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event args for health alerts
    /// </summary>
    public class HealthAlertEventArgs : EventArgs
    {
        public string Message { get; }
        public DateTime Timestamp { get; }

        public HealthAlertEventArgs(string message)
        {
            Message = message;
            Timestamp = DateTime.UtcNow;
        }
    }

    #endregion
}
