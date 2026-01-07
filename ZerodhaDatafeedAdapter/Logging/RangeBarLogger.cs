using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Static logger for RangeATR bar processing events.
    /// Logs to Documents\NinjaTrader 8\ZerodhaAdapter\Logs\{dd-MM-yyyy}\RangeBar.log
    ///
    /// Log Format per bar:
    ///   BarTime, Close, SessionVAH, SessionVAL, SessionPOC, HVNs
    ///
    /// Usage:
    ///   RangeBarLogger.LogBarWithVP(barTime, close, vah, val, poc, hvns);
    /// </summary>
    public static class RangeBarLogger
    {
        private static ILoggerService _logger;
        private static readonly object _lock = new object();

        /// <summary>
        /// Gets the underlying logger service instance.
        /// </summary>
        private static ILoggerService Logger
        {
            get
            {
                if (_logger == null)
                {
                    lock (_lock)
                    {
                        if (_logger == null)
                        {
                            _logger = LoggerFactory.RangeBar;
                        }
                    }
                }
                return _logger;
            }
        }

        /// <summary>
        /// Gets whether debug logging is enabled.
        /// Use this to guard expensive string interpolation in hot paths.
        /// </summary>
        public static bool IsDebugEnabled => Logger?.IsDebugEnabled ?? false;

        /// <summary>
        /// Gets whether info logging is enabled.
        /// </summary>
        public static bool IsInfoEnabled => Logger?.IsInfoEnabled ?? false;

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public static void Debug(string message)
        {
            Logger?.Debug(message);
        }

        /// <summary>
        /// Logs a debug message with exception.
        /// </summary>
        public static void Debug(string message, Exception exception)
        {
            Logger?.Debug(message, exception);
        }

        /// <summary>
        /// Logs an info message.
        /// </summary>
        public static void Info(string message)
        {
            Logger?.Info(message);
        }

        /// <summary>
        /// Logs an info message with exception.
        /// </summary>
        public static void Info(string message, Exception exception)
        {
            Logger?.Info(message, exception);
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public static void Warn(string message)
        {
            Logger?.Warn(message);
        }

        /// <summary>
        /// Logs a warning message with exception.
        /// </summary>
        public static void Warn(string message, Exception exception)
        {
            Logger?.Warn(message, exception);
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        public static void Error(string message)
        {
            Logger?.Error(message);
        }

        /// <summary>
        /// Logs an error message with exception.
        /// </summary>
        public static void Error(string message, Exception exception)
        {
            Logger?.Error(message, exception);
        }

        /// <summary>
        /// Logs a fatal message.
        /// </summary>
        public static void Fatal(string message)
        {
            Logger?.Fatal(message);
        }

        /// <summary>
        /// Logs a fatal message with exception.
        /// </summary>
        public static void Fatal(string message, Exception exception)
        {
            Logger?.Fatal(message, exception);
        }

        /// <summary>
        /// Gets the path to the RangeBar log file.
        /// </summary>
        public static string GetLogFilePath()
        {
            return Logger?.GetLogFilePath() ?? string.Empty;
        }

        #region Convenience Methods for RangeATR Bar Logging

        /// <summary>
        /// Logs a bar with current session VP metrics.
        /// Format: BarTime, Close, SessionVAH, SessionVAL, SessionPOC, HVNs
        /// </summary>
        public static void LogBarWithVP(DateTime barTime, double close, double sessionVAH, double sessionVAL, double sessionPOC, List<double> hvns)
        {
            if (IsInfoEnabled)
            {
                string hvnStr = hvns != null && hvns.Count > 0
                    ? string.Join("|", hvns.ConvertAll(h => h.ToString("F2")))
                    : "-";

                Info($"{barTime:HH:mm:ss},{close:F2},{sessionVAH:F2},{sessionVAL:F2},{sessionPOC:F2},{hvnStr}");
            }
        }

        /// <summary>
        /// Logs a new bar creation event with VP context.
        /// </summary>
        public static void LogBarCreated(string symbol, DateTime time, double open, double high, double low, double close, long volume, int barIndex)
        {
            if (IsDebugEnabled)
            {
                Debug($"[BAR] {symbol} | #{barIndex} | {time:HH:mm:ss} | O={open:F2} H={high:F2} L={low:F2} C={close:F2} | Vol={volume}");
            }
        }

        /// <summary>
        /// Logs a session start/reset event.
        /// </summary>
        public static void LogSessionStart(string symbol, DateTime sessionStart)
        {
            Info($"[SESSION_START] {symbol} | {sessionStart:yyyy-MM-dd HH:mm:ss} | VP Reset");
        }

        /// <summary>
        /// Logs a session reset event (new trading day).
        /// </summary>
        public static void LogSessionReset(string symbol, DateTime newSessionDate)
        {
            Info($"[SESSION_RESET] {symbol} | New session: {newSessionDate:yyyy-MM-dd} | VP cleared");
        }

        /// <summary>
        /// Logs historical data loading progress.
        /// </summary>
        public static void LogHistoricalProgress(string symbol, int barsLoaded, int ticksLoaded, DateTime fromDate, DateTime toDate)
        {
            Info($"[HISTORICAL] {symbol} | Bars={barsLoaded} Ticks={ticksLoaded} | {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}");
        }

        /// <summary>
        /// Logs cache load status.
        /// </summary>
        public static void LogCacheStatus(string symbol, int cachedBars, int cachedTicks, bool fromCache)
        {
            if (fromCache)
                Info($"[CACHE_HIT] {symbol} | Loaded {cachedBars} bars, {cachedTicks} ticks from SQLite cache");
            else
                Info($"[CACHE_MISS] {symbol} | Loading from NinjaTrader BarsRequest...");
        }

        /// <summary>
        /// Logs VP metrics (summary format).
        /// </summary>
        public static void LogVPMetrics(string symbol, double poc, double vah, double val, double vwap, int hvnCount, int barCount)
        {
            if (IsInfoEnabled)
            {
                Info($"[VP_METRICS] {symbol} | POC={poc:F2} VAH={vah:F2} VAL={val:F2} VWAP={vwap:F2} | HVNs={hvnCount} Bars={barCount}");
            }
        }

        #endregion
    }
}
