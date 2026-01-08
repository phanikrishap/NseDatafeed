using System;
using System.IO;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Dedicated logger for ICICI Breeze API operations.
    /// Writes to a separate log file (IciciApi.log) for API request/response tracking.
    ///
    /// This is a facade over LoggerFactory.IciciApi that provides backward compatibility
    /// with existing code while using the unified logging infrastructure.
    /// </summary>
    public static class IciciApiLogger
    {
        // Use the unified LoggerFactory for actual logging
        private static ILoggerService _logger => LoggerFactory.IciciApi;

        public static void Initialize()
        {
            // LoggerFactory handles initialization automatically
            Info("ICICI API Logger initialized");
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

        #region API-specific logging methods

        /// <summary>
        /// Log API request details
        /// </summary>
        public static void LogRequest(string endpoint, string method, string parameters = null)
        {
            var msg = $"[REQUEST] {method} {endpoint}";
            if (!string.IsNullOrEmpty(parameters))
                msg += $" | Params: {parameters}";
            Info(msg);
        }

        /// <summary>
        /// Log API response details
        /// </summary>
        public static void LogResponse(string endpoint, bool success, int recordCount, long elapsedMs, string error = null)
        {
            if (success)
            {
                Info($"[RESPONSE] {endpoint} | Success | Records: {recordCount} | Time: {elapsedMs}ms");
            }
            else
            {
                Error($"[RESPONSE] {endpoint} | Failed | Error: {error} | Time: {elapsedMs}ms");
            }
        }

        /// <summary>
        /// Log session generation attempt
        /// </summary>
        public static void LogSessionGeneration(bool success, string message)
        {
            if (success)
                Info($"[SESSION] Generated successfully: {message}");
            else
                Error($"[SESSION] Generation failed: {message}");
        }

        /// <summary>
        /// Log historical data download progress
        /// </summary>
        public static void LogDownloadProgress(string symbol, int strike, string optionType,
            int chunkNumber, int totalChunks, int recordsInChunk, long elapsedMs)
        {
            Info($"[DOWNLOAD] {symbol} {strike}{optionType} | Chunk {chunkNumber}/{totalChunks} | Records: {recordsInChunk} | Time: {elapsedMs}ms");
        }

        /// <summary>
        /// Log strike download completion
        /// </summary>
        public static void LogStrikeComplete(string symbol, int strike, string optionType,
            int totalRecords, long totalElapsedMs)
        {
            Info($"[COMPLETE] {symbol} {strike}{optionType} | Total Records: {totalRecords} | Total Time: {totalElapsedMs}ms");
        }

        /// <summary>
        /// Log batch download start
        /// </summary>
        public static void LogBatchStart(string symbol, DateTime expiry, int strikeCount, int parallelStrikes)
        {
            Info($"[BATCH START] {symbol} Expiry={expiry:dd-MMM-yy} | Strikes: {strikeCount} | Parallel: {parallelStrikes}");
        }

        /// <summary>
        /// Log batch download completion
        /// </summary>
        public static void LogBatchComplete(string symbol, DateTime expiry, int successCount, int failCount, long totalElapsedMs)
        {
            Info($"[BATCH COMPLETE] {symbol} Expiry={expiry:dd-MMM-yy} | Success: {successCount} | Failed: {failCount} | Total Time: {totalElapsedMs / 1000.0:F1}s");
        }

        /// <summary>
        /// Log rate limiting delay
        /// </summary>
        public static void LogRateLimit(int delayMs)
        {
            Debug($"[RATE LIMIT] Waiting {delayMs}ms before next request");
        }

        /// <summary>
        /// Log API error with full details
        /// </summary>
        public static void LogApiError(string endpoint, string errorCode, string errorMessage, Exception ex = null)
        {
            var msg = $"[API ERROR] {endpoint} | Code: {errorCode} | Message: {errorMessage}";
            if (ex != null)
                Error(msg, ex);
            else
                Error(msg);
        }

        #endregion

        /// <summary>
        /// Gets the full path to the current ICICI API log file
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logger.GetLogFilePath();
        }

        /// <summary>
        /// Opens the ICICI API log file in the default text editor
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
                    Logger.Warn($"[IciciApiLogger] Log file not found at: {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IciciApiLogger] Failed to open log file: {ex.Message}");
            }
        }
    }
}
