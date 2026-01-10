using System;
using System.Threading;
using ZerodhaDatafeedAdapter.Models.MarketData;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Monitors the health of interest processing and alerts on anomalies.
    /// </summary>
    public class TickProcessorHealthMonitor : IDisposable
    {
        private readonly int _warningThreshold;
        private readonly int _criticalThreshold;
        private readonly Func<HealthMetricsSnapshot> _metricsProvider;
        private readonly System.Timers.Timer _monitorTimer;
        private readonly object _lock = new object();
        private bool _isDisposed;

        public event Action<string, HealthMetricsSnapshot> HealthAlert;

        public TickProcessorHealthMonitor(int warning, int critical, Func<HealthMetricsSnapshot> metricsProvider, int intervalMs = 30000)
        {
            _warningThreshold = warning;
            _criticalThreshold = critical;
            _metricsProvider = metricsProvider;
            
            _monitorTimer = new System.Timers.Timer(intervalMs);
            _monitorTimer.Elapsed += (s, e) => CheckHealth();
            _monitorTimer.AutoReset = true;
        }

        public void Start() => _monitorTimer.Start();
        public void Stop() => _monitorTimer.Stop();

        private void CheckHealth()
        {
            try
            {
                var metrics = _metricsProvider();
                if (metrics == null) return;

                // Example health checks
                if (metrics.TicksQueued > _criticalThreshold)
                {
                    HealthAlert?.Invoke($"CRITICAL: Queue depth ({metrics.TicksQueued}) exceeds threshold!", metrics);
                }
                else if (metrics.TicksQueued > _warningThreshold)
                {
                    HealthAlert?.Invoke($"WARNING: Queue depth ({metrics.TicksQueued}) is high.", metrics);
                }

                if (metrics.CallbackErrors > 100)
                {
                    HealthAlert?.Invoke($"WARNING: High callback error rate detected ({metrics.CallbackErrors})", metrics);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[TPHM] Error in health check: {ex.Message}");
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                _monitorTimer?.Stop();
                _monitorTimer?.Dispose();
            }
        }
    }

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
}
