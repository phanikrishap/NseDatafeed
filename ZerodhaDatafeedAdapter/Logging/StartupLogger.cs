using System;
using System.Diagnostics;
using System.IO;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Dedicated logger for critical startup events.
    /// Writes to a separate log file to ensure startup issues are easily diagnosed.
    /// Critical for automated trading - startup failures mean missed opportunities for the day.
    ///
    /// Logs include:
    /// - Adapter initialization
    /// - Token validation/generation
    /// - WebSocket connection establishment
    /// - Market data service initialization
    /// - Instrument registration
    /// - First tick reception (proof of data flow)
    /// </summary>
    public static class StartupLogger
    {
        private static readonly ILog _log;
        private static readonly string _logFolderPath;
        private static readonly string _logFilePath;
        private static bool _initialized = false;
        private static readonly object _lockObject = new object();
        private static readonly Stopwatch _startupStopwatch = new Stopwatch();
        private static DateTime _adapterStartTime;

        static StartupLogger()
        {
            try
            {
                _adapterStartTime = DateTime.Now;
                _startupStopwatch.Start();

                // Get the user's Documents folder path
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _logFolderPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "Logs");

                // Create the log directory if it doesn't exist
                if (!Directory.Exists(_logFolderPath))
                {
                    Directory.CreateDirectory(_logFolderPath);
                }

                // Create log file name with date - dedicated Startup file
                string fileName = $"Startup_{DateTime.Now:yyyy-MM-dd}.log";
                _logFilePath = Path.Combine(_logFolderPath, fileName);

                // Delete existing startup log file on startup to start fresh each session
                CleanupOldLogFile(_logFilePath);

                // Configure the startup-specific logger
                ConfigureStartupLogger();

                // Get logger instance with a unique name
                _log = LogManager.GetLogger("StartupLogger");
                _initialized = true;

                // Write header
                WriteSessionHeader();
            }
            catch (Exception ex)
            {
                // Log to main logger if startup logger fails to initialize
                Logger.Error($"[StartupLogger] Failed to initialize: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Delete existing log file on startup to start fresh each trading session
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

        private static void ConfigureStartupLogger()
        {
            try
            {
                var hierarchy = (Hierarchy)LogManager.GetRepository();

                // Create a dedicated appender for Startup logs
                var roller = new RollingFileAppender
                {
                    Name = "StartupRollingFileAppender",
                    File = _logFilePath,
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Date,
                    DatePattern = "yyyyMMdd",
                    LockingModel = new FileAppender.MinimalLock(),
                    MaxSizeRollBackups = 10,
                    MaximumFileSize = "5MB"
                };

                // Create and set the layout with detailed timestamp for startup events
                var patternLayout = new PatternLayout
                {
                    ConversionPattern = "%date{HH:mm:ss.fff} [%thread] %-5level - %message%newline"
                };
                patternLayout.ActivateOptions();
                roller.Layout = patternLayout;

                // Activate the appender
                roller.ActivateOptions();

                // Create a specific logger for Startup
                var startupLogger = hierarchy.GetLogger("StartupLogger") as log4net.Repository.Hierarchy.Logger;
                if (startupLogger != null)
                {
                    startupLogger.RemoveAllAppenders();
                    startupLogger.AddAppender(roller);
                    startupLogger.Level = Level.Debug;
                    startupLogger.Additivity = false; // Don't propagate to root logger
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StartupLogger] Failed to configure logger: {ex.Message}", ex);
            }
        }

        private static void WriteSessionHeader()
        {
            if (!_initialized || _log == null) return;

            _log.Info("================================================================================");
            _log.Info($"  ZERODHA ADAPTER STARTUP LOG - Session Started: {_adapterStartTime:yyyy-MM-dd HH:mm:ss}");
            _log.Info("================================================================================");
            _log.Info($"  Machine: {Environment.MachineName}");
            _log.Info($"  User: {Environment.UserName}");
            _log.Info($"  OS: {Environment.OSVersion}");
            _log.Info($"  CLR: {Environment.Version}");
            _log.Info("================================================================================");
            _log.Info("");
        }

        public static void Initialize()
        {
            lock (_lockObject)
            {
                if (!_initialized)
                {
                    ConfigureStartupLogger();
                    _initialized = true;
                    Info("StartupLogger initialized");
                }
            }
        }

        /// <summary>
        /// Gets elapsed time since adapter start in a readable format
        /// </summary>
        private static string GetElapsedTime()
        {
            return $"+{_startupStopwatch.ElapsedMilliseconds}ms";
        }

        #region Core Log Methods

        public static void Debug(string message)
        {
            if (_initialized && _log != null && _log.IsDebugEnabled)
                _log.Debug($"[{GetElapsedTime()}] {message}");
        }

        public static void Info(string message)
        {
            if (_initialized && _log != null && _log.IsInfoEnabled)
                _log.Info($"[{GetElapsedTime()}] {message}");
        }

        public static void Warn(string message)
        {
            if (_initialized && _log != null && _log.IsWarnEnabled)
                _log.Warn($"[{GetElapsedTime()}] {message}");
        }

        public static void Error(string message)
        {
            if (_initialized && _log != null)
                _log.Error($"[{GetElapsedTime()}] {message}");
        }

        public static void Error(string message, Exception exception)
        {
            if (_initialized && _log != null)
                _log.Error($"[{GetElapsedTime()}] {message}", exception);
        }

        #endregion

        #region Startup Phase Logging

        /// <summary>
        /// Log adapter initialization start
        /// </summary>
        public static void LogAdapterInit(string version)
        {
            Info("========== ADAPTER INITIALIZATION ==========");
            Info($"ZerodhaDatafeedAdapter v{version} initializing...");
        }

        /// <summary>
        /// Log configuration load result
        /// </summary>
        public static void LogConfigurationLoad(bool success, string configPath = null)
        {
            if (success)
            {
                Info($"Configuration loaded successfully" + (configPath != null ? $" from: {configPath}" : ""));
            }
            else
            {
                Warn("Configuration load failed - using defaults");
            }
        }

        /// <summary>
        /// Log token validation phase
        /// </summary>
        public static void LogTokenValidationStart()
        {
            Info("========== TOKEN VALIDATION ==========");
            Info("Starting access token validation...");
        }

        /// <summary>
        /// Log token validation result
        /// </summary>
        public static void LogTokenValidationResult(bool success, string details = null)
        {
            if (success)
            {
                Info($"Access token is VALID" + (details != null ? $" | {details}" : ""));
            }
            else
            {
                Error($"Access token validation FAILED" + (details != null ? $" | {details}" : ""));
            }
        }

        /// <summary>
        /// Log token generation attempt
        /// </summary>
        public static void LogTokenGeneration(string method, bool success, string error = null)
        {
            if (success)
            {
                Info($"Token generated successfully via {method}");
            }
            else
            {
                Error($"Token generation FAILED via {method}" + (error != null ? $" | Error: {error}" : ""));
            }
        }

        /// <summary>
        /// Log WebSocket connection phase
        /// </summary>
        public static void LogWebSocketPhase(string phase, bool success, string details = null)
        {
            string msg = $"[WebSocket] {phase}";
            if (details != null) msg += $" | {details}";

            if (success)
            {
                Info(msg);
            }
            else
            {
                Error(msg);
            }
        }

        /// <summary>
        /// Log WebSocket connection established
        /// </summary>
        public static void LogWebSocketConnected(int subscriptionCount = 0)
        {
            Info("========== WEBSOCKET CONNECTED ==========");
            Info($"WebSocket connection established successfully");
            if (subscriptionCount > 0)
            {
                Info($"Active subscriptions: {subscriptionCount}");
            }
        }

        /// <summary>
        /// Log WebSocket connection failed
        /// </summary>
        public static void LogWebSocketFailed(string reason)
        {
            Error("========== WEBSOCKET FAILED ==========");
            Error($"WebSocket connection FAILED: {reason}");
        }

        /// <summary>
        /// Log first tick received (proof of data flow)
        /// </summary>
        public static void LogFirstTickReceived(string symbol, double price)
        {
            Info("========== FIRST TICK RECEIVED ==========");
            Info($"Data flow confirmed! First tick: {symbol} @ {price:F2}");
            Info($"Time to first tick: {_startupStopwatch.ElapsedMilliseconds}ms from adapter start");
        }

        /// <summary>
        /// Log market data service initialization
        /// </summary>
        public static void LogMarketDataServiceInit(bool success, string details = null)
        {
            if (success)
            {
                Info($"MarketDataService initialized successfully" + (details != null ? $" | {details}" : ""));
            }
            else
            {
                Error($"MarketDataService initialization FAILED" + (details != null ? $" | {details}" : ""));
            }
        }

        /// <summary>
        /// Log instrument registration
        /// </summary>
        public static void LogInstrumentRegistration(int count, string segment = null)
        {
            string msg = $"Registered {count} instruments";
            if (segment != null) msg += $" for segment: {segment}";
            Info(msg);
        }

        /// <summary>
        /// Log subscription request
        /// </summary>
        public static void LogSubscription(string symbol, int token, bool isIndex, bool success)
        {
            string type = isIndex ? "INDEX" : "STOCK";
            if (success)
            {
                Info($"[Subscribe] {type} {symbol} (token={token}) - SUCCESS");
            }
            else
            {
                Warn($"[Subscribe] {type} {symbol} (token={token}) - QUEUED (connection pending)");
            }
        }

        /// <summary>
        /// Log connection check result
        /// </summary>
        public static void LogConnectionCheck(bool success, string apiName = "Zerodha")
        {
            if (success)
            {
                Info($"{apiName} API connection check: PASSED");
            }
            else
            {
                Error($"{apiName} API connection check: FAILED");
            }
        }

        /// <summary>
        /// Log startup completion
        /// </summary>
        public static void LogStartupComplete(bool success, int subscriptionCount = 0)
        {
            Info("");
            Info("================================================================================");
            if (success)
            {
                Info($"  STARTUP COMPLETE - Ready for trading");
                Info($"  Total startup time: {_startupStopwatch.ElapsedMilliseconds}ms");
                Info($"  Active subscriptions: {subscriptionCount}");
            }
            else
            {
                Error($"  STARTUP INCOMPLETE - Trading may be impacted!");
                Error($"  Please check errors above and restart if necessary");
            }
            Info("================================================================================");
            Info("");
        }

        /// <summary>
        /// Log a critical error that could prevent trading
        /// </summary>
        public static void LogCriticalError(string component, string error, string impact = null)
        {
            Error("");
            Error("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Error($"  CRITICAL ERROR in {component}");
            Error($"  Error: {error}");
            if (impact != null)
            {
                Error($"  Impact: {impact}");
            }
            Error("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
            Error("");
        }

        /// <summary>
        /// Log a milestone with timing
        /// </summary>
        public static void LogMilestone(string milestone)
        {
            Info($"[MILESTONE] {milestone}");
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets the full path to the current startup log file
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logFilePath;
        }

        /// <summary>
        /// Opens the startup log file in the default text editor
        /// </summary>
        public static void OpenLogFile()
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    Process.Start(_logFilePath);
                }
                else
                {
                    Logger.Warn($"[StartupLogger] Log file not found at: {_logFilePath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StartupLogger] Failed to open log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the elapsed time since adapter start
        /// </summary>
        public static long GetElapsedMilliseconds()
        {
            return _startupStopwatch.ElapsedMilliseconds;
        }

        #endregion
    }
}
