using System;
using System.Diagnostics;
using System.IO;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Dedicated logger for critical startup events.
    /// Writes to a separate log file (Startup_{date}.log) to ensure startup issues are easily diagnosed.
    /// Critical for automated trading - startup failures mean missed opportunities for the day.
    ///
    /// This is a facade over LoggerFactory.Startup that provides backward compatibility
    /// with existing code while using the unified logging infrastructure.
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
        // Use the unified LoggerFactory for actual logging
        private static ILoggerService _logger => LoggerFactory.Startup;

        private static readonly Stopwatch _startupStopwatch = new Stopwatch();
        private static DateTime _adapterStartTime;
        private static bool _headerWritten = false;
        private static readonly object _lockObject = new object();

        static StartupLogger()
        {
            _adapterStartTime = DateTime.Now;
            _startupStopwatch.Start();
            WriteSessionHeader();
        }

        private static void WriteSessionHeader()
        {
            lock (_lockObject)
            {
                if (_headerWritten) return;
                _headerWritten = true;

                _logger.Info("================================================================================");
                _logger.Info($"  ZERODHA ADAPTER STARTUP LOG - Session Started: {_adapterStartTime:yyyy-MM-dd HH:mm:ss}");
                _logger.Info("================================================================================");
                _logger.Info($"  Machine: {Environment.MachineName}");
                _logger.Info($"  User: {Environment.UserName}");
                _logger.Info($"  OS: {Environment.OSVersion}");
                _logger.Info($"  CLR: {Environment.Version}");
                _logger.Info("================================================================================");
                _logger.Info("");
            }
        }

        public static void Initialize()
        {
            // LoggerFactory handles initialization automatically
            Info("StartupLogger initialized");
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
            _logger.Debug($"[{GetElapsedTime()}] {message}");
        }

        public static void Info(string message)
        {
            _logger.Info($"[{GetElapsedTime()}] {message}");
        }

        public static void Warn(string message)
        {
            _logger.Warn($"[{GetElapsedTime()}] {message}");
        }

        public static void Error(string message)
        {
            _logger.Error($"[{GetElapsedTime()}] {message}");
        }

        public static void Error(string message, Exception exception)
        {
            _logger.Error($"[{GetElapsedTime()}] {message}", exception);
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
            return _logger.GetLogFilePath();
        }

        /// <summary>
        /// Opens the startup log file in the default text editor
        /// </summary>
        public static void OpenLogFile()
        {
            try
            {
                string logFilePath = _logger.GetLogFilePath();
                if (File.Exists(logFilePath))
                {
                    Process.Start(logFilePath);
                }
                else
                {
                    Logger.Warn($"[StartupLogger] Log file not found at: {logFilePath}");
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
