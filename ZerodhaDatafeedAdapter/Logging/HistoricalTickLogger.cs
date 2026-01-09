using System;
using System.IO;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Unified logger for Historical Tick Data operations (both ICICI and Accelpix).
    /// Writes to a single log file (HistoricalTick.log) for all tick data source operations.
    ///
    /// This replaces the separate HistoricalTickLogger and HistoricalTickLogger to provide
    /// a unified view of historical tick data operations regardless of source.
    /// </summary>
    public static class HistoricalTickLogger
    {
        // Use the unified LoggerFactory for actual logging
        private static ILoggerService _logger => LoggerFactory.HistoricalTick;

        public static void Initialize()
        {
            // LoggerFactory handles initialization automatically
            Info("[HistoricalTickLogger] Historical Tick Data Logger initialized");
        }

        #region Log methods

        public static bool IsDebugEnabled => _logger.IsDebugEnabled;

        public static void Debug(string message)
        {
            _logger.Debug(message);
        }

        public static void Debug(string message, Exception exception)
        {
            _logger.Debug(message, exception);
        }

        public static void Info(string message)
        {
            _logger.Info(message);
        }

        public static void Info(string message, Exception exception)
        {
            _logger.Info(message, exception);
        }

        public static void Warn(string message)
        {
            _logger.Warn(message);
        }

        public static void Warn(string message, Exception exception)
        {
            _logger.Warn(message, exception);
        }

        public static void Error(string message)
        {
            _logger.Error(message);
        }

        public static void Error(string message, Exception exception)
        {
            _logger.Error(message, exception);
        }

        #endregion

        #region Source-specific logging methods

        /// <summary>
        /// Log API request details with source identifier
        /// </summary>
        public static void LogRequest(string source, string endpoint, string method, string parameters = null)
        {
            var msg = $"[{source}] [REQUEST] {method} {endpoint}";
            if (!string.IsNullOrEmpty(parameters))
                msg += $" | Params: {parameters}";
            Info(msg);
        }

        /// <summary>
        /// Log API response details with source identifier
        /// </summary>
        public static void LogResponse(string source, string endpoint, bool success, int recordCount, long elapsedMs, string error = null)
        {
            if (success)
            {
                Info($"[{source}] [RESPONSE] {endpoint} | Success | Records: {recordCount} | Time: {elapsedMs}ms");
            }
            else
            {
                Error($"[{source}] [RESPONSE] {endpoint} | Failed | Error: {error} | Time: {elapsedMs}ms");
            }
        }

        /// <summary>
        /// Log session generation attempt (ICICI specific)
        /// </summary>
        public static void LogSessionGeneration(bool success, string message)
        {
            if (success)
                Info($"[ICICI] [SESSION] Generated successfully: {message}");
            else
                Error($"[ICICI] [SESSION] Generation failed: {message}");
        }

        /// <summary>
        /// Log historical data download progress
        /// </summary>
        public static void LogDownloadProgress(string source, string symbol, DateTime tradeDate, int tickCount, long elapsedMs)
        {
            Info($"[{source}] [DOWNLOAD] {symbol} {tradeDate:yyyy-MM-dd} | Ticks: {tickCount} | Time: {elapsedMs}ms");
        }

        /// <summary>
        /// Log download completion
        /// </summary>
        public static void LogDownloadComplete(string source, string symbol, DateTime tradeDate, int totalTicks, long totalElapsedMs)
        {
            Info($"[{source}] [COMPLETE] {symbol} {tradeDate:yyyy-MM-dd} | Total Ticks: {totalTicks} | Total Time: {totalElapsedMs}ms");
        }

        /// <summary>
        /// Log symbol mapping operation
        /// </summary>
        public static void LogSymbolMapping(string zerodhaSymbol, string targetSymbol, bool success)
        {
            if (success)
            {
                Debug($"[SYMBOL-MAP] {zerodhaSymbol} -> {targetSymbol}");
            }
            else
            {
                Warn($"[SYMBOL-MAP] Failed to map: {zerodhaSymbol}");
            }
        }

        /// <summary>
        /// Log batch download start
        /// </summary>
        public static void LogBatchStart(string source, string symbol, DateTime expiry, int strikeCount, int parallelRequests)
        {
            Info($"[{source}] [BATCH START] {symbol} Expiry={expiry:dd-MMM-yy} | Strikes: {strikeCount} | Parallel: {parallelRequests}");
        }

        /// <summary>
        /// Log batch download completion
        /// </summary>
        public static void LogBatchComplete(string source, string symbol, DateTime expiry, int successCount, int failCount, long totalElapsedMs)
        {
            Info($"[{source}] [BATCH COMPLETE] {symbol} Expiry={expiry:dd-MMM-yy} | Success: {successCount} | Failed: {failCount} | Total Time: {totalElapsedMs / 1000.0:F1}s");
        }

        /// <summary>
        /// Log rate limiting delay
        /// </summary>
        public static void LogRateLimit(string source, int delayMs)
        {
            Debug($"[{source}] [RATE LIMIT] Waiting {delayMs}ms before next request");
        }

        /// <summary>
        /// Log cache hit
        /// </summary>
        public static void LogCacheHit(string symbol, DateTime tradeDate, int tickCount)
        {
            Info($"[CACHE] HIT: {symbol} {tradeDate:yyyy-MM-dd} | Ticks: {tickCount}");
        }

        /// <summary>
        /// Log cache miss
        /// </summary>
        public static void LogCacheMiss(string symbol, DateTime tradeDate)
        {
            Debug($"[CACHE] MISS: {symbol} {tradeDate:yyyy-MM-dd}");
        }

        /// <summary>
        /// Log cache write
        /// </summary>
        public static void LogCacheWrite(string symbol, DateTime tradeDate, int tickCount)
        {
            Info($"[CACHE] WRITE: {symbol} {tradeDate:yyyy-MM-dd} | Ticks: {tickCount}");
        }

        /// <summary>
        /// Log API error with full details
        /// </summary>
        public static void LogApiError(string source, string endpoint, string errorCode, string errorMessage, Exception ex = null)
        {
            var msg = $"[{source}] [API ERROR] {endpoint} | Code: {errorCode} | Message: {errorMessage}";
            if (ex != null)
                Error(msg, ex);
            else
                Error(msg);
        }

        /// <summary>
        /// Log strike download completion (ICICI specific legacy method)
        /// </summary>
        public static void LogStrikeComplete(string symbol, int strike, string optionType, int totalRecords, long totalElapsedMs)
        {
            Info($"[ICICI] [COMPLETE] {symbol} {strike}{optionType} | Total Records: {totalRecords} | Total Time: {totalElapsedMs}ms");
        }

        #endregion

        /// <summary>
        /// Gets the full path to the current Historical Tick Data log file
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logger.GetLogFilePath();
        }

        /// <summary>
        /// Opens the Historical Tick Data log file in the default text editor
        /// </summary>
        public static void OpenLogFile()
        {
            try
            {
                string logFilePath = _logger.GetLogFilePath();
                if (File.Exists(logFilePath))
                {
                    System.Diagnostics.Process.Start(logFilePath);
                }
                else
                {
                    Logger.Warn($"[HistoricalTickLogger] Log file not found at: {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalTickLogger] Failed to open log file: {ex.Message}");
            }
        }
    }
}
