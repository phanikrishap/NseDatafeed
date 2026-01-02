using System;
using System.Collections.Concurrent;
using System.IO;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Factory for creating and managing domain-specific loggers.
    /// Each domain (TBS, Startup, TickVolume, etc.) gets its own log file.
    ///
    /// Usage:
    ///   var tbsLogger = LoggerFactory.GetLogger(LogDomain.TBS);
    ///   tbsLogger.Info("Tranche executed");
    /// </summary>
    public static class LoggerFactory
    {
        private static readonly ConcurrentDictionary<LogDomain, ILoggerService> _loggers
            = new ConcurrentDictionary<LogDomain, ILoggerService>();

        private static readonly object _initLock = new object();
        private static bool _initialized = false;
        private static string _logFolderPath;

        /// <summary>
        /// Gets or creates a logger for the specified domain.
        /// Thread-safe and lazy-initialized.
        /// </summary>
        public static ILoggerService GetLogger(LogDomain domain)
        {
            EnsureInitialized();
            return _loggers.GetOrAdd(domain, CreateLogger);
        }

        /// <summary>
        /// Gets the main application logger (default domain).
        /// </summary>
        public static ILoggerService Main => GetLogger(LogDomain.Main);

        /// <summary>
        /// Gets the TBS (Time-Based Straddle) logger.
        /// </summary>
        public static ILoggerService TBS => GetLogger(LogDomain.TBS);

        /// <summary>
        /// Gets the Startup logger for initialization events.
        /// </summary>
        public static ILoggerService Startup => GetLogger(LogDomain.Startup);

        /// <summary>
        /// Gets the folder path where all logs are stored.
        /// </summary>
        public static string LogFolderPath
        {
            get
            {
                EnsureInitialized();
                return _logFolderPath;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // Get the user's Documents folder path
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _logFolderPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "Logs");

                // Create the log directory if it doesn't exist
                if (!Directory.Exists(_logFolderPath))
                {
                    Directory.CreateDirectory(_logFolderPath);
                }

                _initialized = true;
            }
        }

        private static ILoggerService CreateLogger(LogDomain domain)
        {
            return new DomainLogger(domain, _logFolderPath);
        }

        /// <summary>
        /// Opens the log folder in Windows Explorer.
        /// </summary>
        public static void OpenLogFolder()
        {
            EnsureInitialized();
            if (Directory.Exists(_logFolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", _logFolderPath);
            }
        }
    }

    /// <summary>
    /// Defines the logging domains (each gets a separate log file).
    /// </summary>
    public enum LogDomain
    {
        /// <summary>Main application log (ZerodhaAdapter_{date}.log)</summary>
        Main,

        /// <summary>TBS Manager log (TBS_{date}.log)</summary>
        TBS,

        /// <summary>Startup/initialization log (Startup_{date}.log)</summary>
        Startup,

        /// <summary>Tick volume analysis log (TickVolume_{date}.csv)</summary>
        TickVolume,

        /// <summary>Synthetic straddle log (SyntheticData_{date}.csv)</summary>
        Synthetic,

        /// <summary>WebSocket connection log</summary>
        WebSocket,

        /// <summary>Market data processing log</summary>
        MarketData
    }

    /// <summary>
    /// Domain-specific logger implementation using log4net.
    /// Each instance writes to its own log file based on domain.
    /// </summary>
    internal class DomainLogger : ILoggerService
    {
        private readonly ILog _log;
        private readonly string _logFilePath;
        private readonly LogDomain _domain;
        private readonly bool _initialized;

        public DomainLogger(LogDomain domain, string logFolderPath)
        {
            _domain = domain;

            try
            {
                // Create log file name based on domain
                string fileName = GetLogFileName(domain);
                _logFilePath = Path.Combine(logFolderPath, fileName);

                // Clean up old log file on startup for non-main loggers
                if (domain != LogDomain.Main)
                {
                    CleanupOldLogFile(_logFilePath);
                }

                // Configure the domain-specific logger
                ConfigureLogger(domain);

                // Get logger instance with domain-specific name
                _log = LogManager.GetLogger($"ZerodhaAdapter.{domain}");
                _initialized = true;
            }
            catch (Exception ex)
            {
                // Fall back to main logger if domain logger fails
                Logger.Error($"[LoggerFactory] Failed to initialize {domain} logger: {ex.Message}", ex);
                _log = LogManager.GetLogger(typeof(DomainLogger));
                _initialized = false;
            }
        }

        private string GetLogFileName(LogDomain domain)
        {
            string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            return domain switch
            {
                LogDomain.Main => $"ZerodhaAdapter_{dateStr}.log",
                LogDomain.TBS => $"TBS_{dateStr}.log",
                LogDomain.Startup => $"Startup_{dateStr}.log",
                LogDomain.TickVolume => $"TickVolume_{dateStr}.csv",
                LogDomain.Synthetic => $"SyntheticData_{dateStr}.csv",
                LogDomain.WebSocket => $"WebSocket_{dateStr}.log",
                LogDomain.MarketData => $"MarketData_{dateStr}.log",
                _ => $"ZerodhaAdapter_{dateStr}.log"
            };
        }

        private void CleanupOldLogFile(string logFilePath)
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

        private void ConfigureLogger(LogDomain domain)
        {
            try
            {
                var hierarchy = (Hierarchy)LogManager.GetRepository();

                // Create a dedicated appender for this domain
                var roller = new RollingFileAppender
                {
                    Name = $"{domain}RollingFileAppender",
                    File = _logFilePath,
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Date,
                    DatePattern = "yyyyMMdd",
                    LockingModel = new FileAppender.MinimalLock(),
                    MaxSizeRollBackups = 10,
                    MaximumFileSize = "10MB"
                };

                // Create and set the layout
                var patternLayout = new PatternLayout
                {
                    ConversionPattern = "%date{HH:mm:ss.fff} [%thread] %-5level - %message%newline"
                };
                patternLayout.ActivateOptions();
                roller.Layout = patternLayout;

                // Activate the appender
                roller.ActivateOptions();

                // Create a specific logger for this domain
                var domainLogger = hierarchy.GetLogger($"ZerodhaAdapter.{domain}") as log4net.Repository.Hierarchy.Logger;
                if (domainLogger != null)
                {
                    domainLogger.RemoveAllAppenders();
                    domainLogger.AddAppender(roller);
                    domainLogger.Level = Level.Debug;
                    domainLogger.Additivity = false; // Don't propagate to root logger
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[LoggerFactory] Failed to configure {domain} logger: {ex.Message}", ex);
            }
        }

        #region ILoggerService Implementation

        public bool IsDebugEnabled => _initialized && _log != null && _log.IsDebugEnabled;

        public bool IsInfoEnabled => _initialized && _log != null && _log.IsInfoEnabled;

        public void Debug(string message)
        {
            if (_initialized && _log != null && _log.IsDebugEnabled)
                _log.Debug(message);
        }

        public void Debug(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsDebugEnabled)
                _log.Debug(message, exception);
        }

        public void Info(string message)
        {
            if (_initialized && _log != null && _log.IsInfoEnabled)
                _log.Info(message);
        }

        public void Info(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsInfoEnabled)
                _log.Info(message, exception);
        }

        public void Warn(string message)
        {
            if (_initialized && _log != null && _log.IsWarnEnabled)
                _log.Warn(message);
        }

        public void Warn(string message, Exception exception)
        {
            if (_initialized && _log != null && _log.IsWarnEnabled)
                _log.Warn(message, exception);
        }

        public void Error(string message)
        {
            if (_initialized && _log != null)
                _log.Error(message);
        }

        public void Error(string message, Exception exception)
        {
            if (_initialized && _log != null)
                _log.Error(message, exception);
        }

        public void Fatal(string message)
        {
            if (_initialized && _log != null)
                _log.Fatal(message);
        }

        public void Fatal(string message, Exception exception)
        {
            if (_initialized && _log != null)
                _log.Fatal(message, exception);
        }

        public string GetLogFilePath() => _logFilePath;

        #endregion
    }
}
