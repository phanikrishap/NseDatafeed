using System;
using System.IO;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Dedicated logger for TBS (Time-Based Straddle) Manager.
    /// Writes to a separate log file (TBS_{date}.log) to reduce noise in the main log.
    ///
    /// This is a facade over LoggerFactory.TBS that provides backward compatibility
    /// with existing code while using the unified logging infrastructure.
    /// </summary>
    public static class TBSLogger
    {
        // Use the unified LoggerFactory for actual logging
        private static ILoggerService _logger => LoggerFactory.TBS;

        public static void Initialize()
        {
            // LoggerFactory handles initialization automatically
            Info("TBS Logger initialized");
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

        /// <summary>
        /// Log a tranche state change with full context
        /// </summary>
        public static void LogTrancheState(int trancheId, string underlying, string status,
            decimal combinedPnL, decimal cumulativePnL, string additionalInfo = null)
        {
            var msg = $"[Tranche #{trancheId}] {underlying} | Status={status} | CombPnL={combinedPnL:F2} | CumPnL={cumulativePnL:F2}";
            if (!string.IsNullOrEmpty(additionalInfo))
                msg += $" | {additionalInfo}";
            Info(msg);
        }

        /// <summary>
        /// Log a leg state with pricing details
        /// </summary>
        public static void LogLegState(int trancheId, string optionType, string symbol,
            decimal entryPrice, decimal currentPrice, decimal slPrice, string status)
        {
            Info($"[Tranche #{trancheId}] {optionType} Leg | Symbol={symbol} | Entry={entryPrice:F2} | Current={currentPrice:F2} | SL={slPrice:F2} | Status={status}");
        }

        /// <summary>
        /// Log a status transition
        /// </summary>
        public static void LogStatusTransition(int trancheId, string fromStatus, string toStatus, string reason = null)
        {
            var msg = $"[Tranche #{trancheId}] Status transition: {fromStatus} -> {toStatus}";
            if (!string.IsNullOrEmpty(reason))
                msg += $" | Reason: {reason}";
            Info(msg);
        }

        /// <summary>
        /// Log Stoxxo API call
        /// </summary>
        public static void LogStoxxoCall(string apiMethod, string portfolioName, string request, string response)
        {
            Info($"[Stoxxo] {apiMethod} | Portfolio={portfolioName}");
            Debug($"[Stoxxo] Request: {request}");
            Debug($"[Stoxxo] Response: {response}");
        }

        /// <summary>
        /// Log ProfitCondition evaluation
        /// </summary>
        public static void LogProfitConditionCheck(int trancheId, bool profitCondition, decimal cumulativePnL, bool willExecute)
        {
            if (profitCondition)
            {
                if (willExecute)
                    Info($"[Tranche #{trancheId}] ProfitCondition=TRUE | CumPnL={cumulativePnL:F2} > 0 | WILL EXECUTE");
                else
                    Warn($"[Tranche #{trancheId}] ProfitCondition=TRUE | CumPnL={cumulativePnL:F2} <= 0 | SKIPPING");
            }
            else
            {
                Debug($"[Tranche #{trancheId}] ProfitCondition=FALSE | Will execute regardless of CumPnL");
            }
        }

        /// <summary>
        /// Gets the full path to the current TBS log file
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logger.GetLogFilePath();
        }

        /// <summary>
        /// Opens the TBS log file in the default text editor
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
                    Logger.Warn($"[TBSLogger] Log file not found at: {logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TBSLogger] Failed to open log file: {ex.Message}");
            }
        }
    }
}
