using System;
using System.IO;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Dedicated logger for TBS (Time-Based Straddle) Manager.
    /// Writes to a separate log file to reduce noise in the main log.
    /// </summary>
    public static class TBSLogger
    {
        private static readonly ILog _log;
        private static readonly string _logFolderPath;
        private static readonly string _logFilePath;
        private static bool _initialized = false;
        private static readonly object _lockObject = new object();

        static TBSLogger()
        {
            try
            {
                // Get the user's Documents folder path
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _logFolderPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "Logs");

                // Create the log directory if it doesn't exist
                if (!Directory.Exists(_logFolderPath))
                {
                    Directory.CreateDirectory(_logFolderPath);
                }

                // Create log file name with date - dedicated TBS file
                string fileName = $"TBS_{DateTime.Now:yyyy-MM-dd}.log";
                _logFilePath = Path.Combine(_logFolderPath, fileName);

                // Delete existing TBS log file on startup to avoid pileup
                CleanupOldLogFile(_logFilePath);

                // Configure the TBS-specific logger
                ConfigureTBSLogger();

                // Get logger instance with a unique name
                _log = LogManager.GetLogger("TBSLogger");
                _initialized = true;
            }
            catch (Exception ex)
            {
                // Log to main logger if TBS logger fails to initialize
                Logger.Error($"[TBSLogger] Failed to initialize: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Delete existing log file on startup to avoid log pileup
        /// </summary>
        private static void CleanupOldLogFile(string logFilePath)
        {
            try
            {
                if (File.Exists(logFilePath))
                {
                    File.Delete(logFilePath);
                }
            }
            catch
            {
                // Ignore - file might be locked by another process
            }
        }

        private static void ConfigureTBSLogger()
        {
            try
            {
                var hierarchy = (Hierarchy)LogManager.GetRepository();

                // Create a dedicated appender for TBS logs
                var roller = new RollingFileAppender
                {
                    Name = "TBSRollingFileAppender",
                    File = _logFilePath,
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Date,
                    DatePattern = "yyyyMMdd",
                    LockingModel = new FileAppender.MinimalLock(),
                    MaxSizeRollBackups = 10,
                    MaximumFileSize = "10MB"
                };

                // Create and set the layout with more detail for TBS logs
                var patternLayout = new PatternLayout
                {
                    ConversionPattern = "%date{HH:mm:ss.fff} [%thread] %-5level - %message%newline"
                };
                patternLayout.ActivateOptions();
                roller.Layout = patternLayout;

                // Activate the appender
                roller.ActivateOptions();

                // Create a specific logger for TBS
                var tbsLogger = hierarchy.GetLogger("TBSLogger") as log4net.Repository.Hierarchy.Logger;
                if (tbsLogger != null)
                {
                    tbsLogger.RemoveAllAppenders();
                    tbsLogger.AddAppender(roller);
                    tbsLogger.Level = Level.Debug; // TBS logs at debug level by default
                    tbsLogger.Additivity = false; // Don't propagate to root logger
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TBSLogger] Failed to configure logger: {ex.Message}", ex);
            }
        }

        public static void Initialize()
        {
            lock (_lockObject)
            {
                if (!_initialized)
                {
                    ConfigureTBSLogger();
                    _initialized = true;
                    Info("TBS Logger initialized");
                }
            }
        }

        #region Log methods

        public static bool IsDebugEnabled => _initialized && _log != null && _log.IsDebugEnabled;

        public static void Debug(string message)
        {
            if (_initialized && _log != null && _log.IsDebugEnabled)
                _log.Debug(message);
        }

        public static void Debug(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsDebugEnabled)
                _log.Debug(message, exception);
        }

        public static void Info(string message)
        {
            if (_initialized && _log != null && _log.IsInfoEnabled)
                _log.Info(message);
        }

        public static void Info(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsInfoEnabled)
                _log.Info(message, exception);
        }

        public static void Warn(string message)
        {
            if (_initialized && _log != null && _log.IsWarnEnabled)
                _log.Warn(message);
        }

        public static void Warn(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsWarnEnabled)
                _log.Warn(message, exception);
        }

        public static void Error(string message)
        {
            if (_initialized && _log != null)
                _log.Error(message);
        }

        public static void Error(string message, Exception exception)
        {
            if (_initialized && _log != null)
                _log.Error(message, exception);
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
            return _logFilePath;
        }

        /// <summary>
        /// Opens the TBS log file in the default text editor
        /// </summary>
        public static void OpenLogFile()
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    System.Diagnostics.Process.Start(_logFilePath);
                }
                else
                {
                    Logger.Warn($"[TBSLogger] Log file not found at: {_logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TBSLogger] Failed to open log file: {ex.Message}");
            }
        }
    }
}
