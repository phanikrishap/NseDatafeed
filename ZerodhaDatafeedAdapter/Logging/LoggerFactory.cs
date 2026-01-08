using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Newtonsoft.Json;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Manages log settings from adapterLog-settings.json.
    /// Supports per-domain log level configuration.
    ///
    /// Settings file location: Documents\NinjaTrader 8\ZerodhaAdapter\adapterLog-settings.json
    ///
    /// Example settings:
    /// {
    ///   "DefaultLogLevel": "INFO",
    ///   "DomainLogLevels": {
    ///     "TBS": "DEBUG",
    ///     "RangeBar": "DEBUG",
    ///     "MarketData": "WARN"
    ///   }
    /// }
    /// </summary>
    public static class LogSettingsManager
    {
        private static readonly object _lock = new object();
        private static LogSettings _settings;
        private static string _settingsFilePath;
        private static DateTime _lastLoadTime = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);

        public const string DefaultLogLevel = "INFO";

        /// <summary>
        /// Gets the path to the settings file.
        /// </summary>
        public static string SettingsFilePath
        {
            get
            {
                if (_settingsFilePath == null)
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    _settingsFilePath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "adapterLog-settings.json");
                }
                return _settingsFilePath;
            }
        }

        /// <summary>
        /// Gets the log level for a specific domain.
        /// Falls back to DefaultLogLevel if not specified.
        /// </summary>
        public static Level GetLogLevel(LogDomain domain)
        {
            var settings = GetSettings();

            // Check for domain-specific level first
            if (settings.DomainLogLevels != null &&
                settings.DomainLogLevels.TryGetValue(domain.ToString(), out string domainLevel))
            {
                return ParseLogLevel(domainLevel);
            }

            // Fall back to default level
            return ParseLogLevel(settings.DefaultLogLevel ?? DefaultLogLevel);
        }

        /// <summary>
        /// Gets the default log level (for main Logger class).
        /// </summary>
        public static Level GetDefaultLogLevel()
        {
            var settings = GetSettings();
            return ParseLogLevel(settings.DefaultLogLevel ?? DefaultLogLevel);
        }

        /// <summary>
        /// Reloads settings from file.
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _settings = null;
                _lastLoadTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Creates a default settings file if it doesn't exist.
        /// </summary>
        public static void CreateDefaultSettingsFile()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(SettingsFilePath);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var defaultSettings = new LogSettings
                    {
                        DefaultLogLevel = DefaultLogLevel,
                        DomainLogLevels = new Dictionary<string, string>
                        {
                            // Example entries (commented in JSON via description)
                        }
                    };

                    string json = JsonConvert.SerializeObject(defaultSettings, Formatting.Indented);
                    File.WriteAllText(SettingsFilePath, json);
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        private static LogSettings GetSettings()
        {
            lock (_lock)
            {
                // Return cached settings if still valid
                if (_settings != null && (DateTime.Now - _lastLoadTime) < CacheExpiry)
                {
                    return _settings;
                }

                // Load from file
                _settings = LoadSettings();
                _lastLoadTime = DateTime.Now;
                return _settings;
            }
        }

        private static LogSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonConvert.DeserializeObject<LogSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // Ignore errors - use defaults
            }

            return new LogSettings { DefaultLogLevel = DefaultLogLevel };
        }

        private static Level ParseLogLevel(string level)
        {
            return level?.ToUpperInvariant() switch
            {
                "DEBUG" => Level.Debug,
                "INFO" => Level.Info,
                "WARN" or "WARNING" => Level.Warn,
                "ERROR" => Level.Error,
                "FATAL" => Level.Fatal,
                "OFF" => Level.Off,
                _ => Level.Info
            };
        }

        /// <summary>
        /// Settings class for JSON deserialization.
        /// </summary>
        private class LogSettings
        {
            /// <summary>
            /// Default log level for all loggers unless overridden.
            /// </summary>
            public string DefaultLogLevel { get; set; }

            /// <summary>
            /// Per-domain log level overrides.
            /// Key: Domain name (e.g., "TBS", "RangeBar", "MarketData")
            /// Value: Log level (DEBUG, INFO, WARN, ERROR, FATAL, OFF)
            /// </summary>
            public Dictionary<string, string> DomainLogLevels { get; set; }
        }
    }


    /// <summary>
    /// Factory for creating and managing domain-specific loggers.
    /// Each domain (TBS, Startup, TickVolume, etc.) gets its own log file.
    ///
    /// Log Structure:
    ///   Documents\NinjaTrader 8\ZerodhaAdapter\Logs\{dd-MM-yyyy}\{Domain}.log
    ///
    /// Features:
    ///   - Date-wise folder structure (dd-MM-yyyy format)
    ///   - Automatic cleanup of current day's logs on startup
    ///   - Automatic cleanup of logs older than 5 calendar days
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
        private static string _baseLogFolderPath;
        private static string _todayLogFolderPath;

        /// <summary>
        /// Number of calendar days to retain logs (including today).
        /// Logs older than this will be automatically deleted.
        /// </summary>
        public const int LogRetentionDays = 5;

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
        /// Gets the RangeBar logger for RangeATR bar processing events.
        /// </summary>
        public static ILoggerService RangeBar => GetLogger(LogDomain.RangeBar);

        /// <summary>
        /// Gets the ICICI API logger for Breeze API operations.
        /// </summary>
        public static ILoggerService IciciApi => GetLogger(LogDomain.IciciApi);

        /// <summary>
        /// Gets today's log folder path where all logs are stored.
        /// Format: Documents\NinjaTrader 8\ZerodhaAdapter\Logs\{dd-MM-yyyy}\
        /// </summary>
        public static string LogFolderPath
        {
            get
            {
                EnsureInitialized();
                return _todayLogFolderPath;
            }
        }

        /// <summary>
        /// Gets the base log folder path (without date subfolder).
        /// Format: Documents\NinjaTrader 8\ZerodhaAdapter\Logs\
        /// </summary>
        public static string BaseLogFolderPath
        {
            get
            {
                EnsureInitialized();
                return _baseLogFolderPath;
            }
        }

        /// <summary>
        /// Gets today's date folder name in dd-MM-yyyy format.
        /// </summary>
        public static string TodayFolderName => DateTime.Now.ToString("dd-MM-yyyy");

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // Get the user's Documents folder path
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _baseLogFolderPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "Logs");

                // Create date-wise subfolder (dd-MM-yyyy format)
                _todayLogFolderPath = Path.Combine(_baseLogFolderPath, TodayFolderName);

                // Create the base log directory if it doesn't exist
                if (!Directory.Exists(_baseLogFolderPath))
                {
                    Directory.CreateDirectory(_baseLogFolderPath);
                }

                // Clean up stale logs (older than retention period)
                CleanupStaleLogs();

                // Clean up today's logs for fresh session
                CleanupTodayLogs();

                // Create today's log directory
                if (!Directory.Exists(_todayLogFolderPath))
                {
                    Directory.CreateDirectory(_todayLogFolderPath);
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Cleans up today's log folder to start fresh.
        /// Called on adapter startup.
        /// </summary>
        private static void CleanupTodayLogs()
        {
            try
            {
                if (Directory.Exists(_todayLogFolderPath))
                {
                    // Delete all files in today's folder
                    foreach (var file in Directory.GetFiles(_todayLogFolderPath))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore - file might be locked
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        /// <summary>
        /// Cleans up log folders older than the retention period.
        /// Deletes folders older than LogRetentionDays calendar days.
        /// </summary>
        private static void CleanupStaleLogs()
        {
            try
            {
                if (!Directory.Exists(_baseLogFolderPath)) return;

                var cutoffDate = DateTime.Now.Date.AddDays(-(LogRetentionDays - 1));
                var directories = Directory.GetDirectories(_baseLogFolderPath);

                foreach (var dir in directories)
                {
                    var folderName = Path.GetFileName(dir);

                    // Try to parse the folder name as a date (dd-MM-yyyy format)
                    if (DateTime.TryParseExact(folderName, "dd-MM-yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime folderDate))
                    {
                        if (folderDate < cutoffDate)
                        {
                            try
                            {
                                Directory.Delete(dir, recursive: true);
                            }
                            catch
                            {
                                // Ignore - folder might be in use
                            }
                        }
                    }
                    else
                    {
                        // Also clean up old-style log files (files directly in Logs folder)
                        // These are legacy files like ZerodhaAdapter_2026-01-07.log
                        CleanupLegacyLogFiles(dir);
                    }
                }

                // Clean up any legacy log files in the base Logs folder itself
                CleanupLegacyLogFilesInBase();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        /// <summary>
        /// Cleans up legacy log files that don't follow the new folder structure.
        /// </summary>
        private static void CleanupLegacyLogFilesInBase()
        {
            try
            {
                var files = Directory.GetFiles(_baseLogFolderPath, "*.log")
                    .Concat(Directory.GetFiles(_baseLogFolderPath, "*.csv"));

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        // Delete files older than retention period
                        if (fileInfo.LastWriteTime.Date < DateTime.Now.Date.AddDays(-(LogRetentionDays - 1)))
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        /// <summary>
        /// Cleans up legacy files in subdirectories that aren't date folders.
        /// </summary>
        private static void CleanupLegacyLogFiles(string directory)
        {
            // Skip if it's actually a valid date folder
            var folderName = Path.GetFileName(directory);
            if (DateTime.TryParseExact(folderName, "dd-MM-yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            {
                return;
            }

            // This is not a date folder - could be legacy structure
            // Don't delete blindly, just leave it
        }

        private static ILoggerService CreateLogger(LogDomain domain)
        {
            return new DomainLogger(domain, _todayLogFolderPath);
        }

        /// <summary>
        /// Opens today's log folder in Windows Explorer.
        /// </summary>
        public static void OpenLogFolder()
        {
            EnsureInitialized();
            if (Directory.Exists(_todayLogFolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", _todayLogFolderPath);
            }
            else if (Directory.Exists(_baseLogFolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", _baseLogFolderPath);
            }
        }

        /// <summary>
        /// Opens the base log folder in Windows Explorer (shows all date folders).
        /// </summary>
        public static void OpenBaseLogFolder()
        {
            EnsureInitialized();
            if (Directory.Exists(_baseLogFolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", _baseLogFolderPath);
            }
        }
    }

    /// <summary>
    /// Defines the logging domains (each gets a separate log file).
    /// Files are stored in date-wise folders: Logs\{dd-MM-yyyy}\{Domain}.log
    /// </summary>
    public enum LogDomain
    {
        /// <summary>Main application log (ZerodhaAdapter.log)</summary>
        Main,

        /// <summary>TBS Manager log (TBS.log)</summary>
        TBS,

        /// <summary>Startup/initialization log (Startup.log)</summary>
        Startup,

        /// <summary>Tick volume analysis log (TickVolume.csv)</summary>
        TickVolume,

        /// <summary>Synthetic straddle log (SyntheticData.csv)</summary>
        Synthetic,

        /// <summary>WebSocket connection log (WebSocket.log)</summary>
        WebSocket,

        /// <summary>Market data processing log (MarketData.log)</summary>
        MarketData,

        /// <summary>RangeATR bar processing log (RangeBar.log)</summary>
        RangeBar,

        /// <summary>ICICI Breeze API log (IciciApi.log)</summary>
        IciciApi
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
            // No date suffix needed - logs are organized in date folders (dd-MM-yyyy)
            return domain switch
            {
                LogDomain.Main => "ZerodhaAdapter.log",
                LogDomain.TBS => "TBS.log",
                LogDomain.Startup => "Startup.log",
                LogDomain.TickVolume => "TickVolume.csv",
                LogDomain.Synthetic => "SyntheticData.csv",
                LogDomain.WebSocket => "WebSocket.log",
                LogDomain.MarketData => "MarketData.log",
                LogDomain.RangeBar => "RangeBar.log",
                LogDomain.IciciApi => "IciciApi.log",
                _ => "ZerodhaAdapter.log"
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
                string appenderName = $"{domain}RollingFileAppender";

                // Get existing logger to check if already configured
                var domainLogger = hierarchy.GetLogger($"ZerodhaAdapter.{domain}") as log4net.Repository.Hierarchy.Logger;
                if (domainLogger == null) return;

                // Check if this appender already exists - avoid duplicate configuration
                var existingAppender = domainLogger.GetAppender(appenderName);
                if (existingAppender != null)
                {
                    // Already configured, just ensure level is set
                    domainLogger.Level = LogSettingsManager.GetLogLevel(domain);
                    return;
                }

                // Remove any other appenders first
                domainLogger.RemoveAllAppenders();

                // Create a dedicated appender for this domain
                // Use Size-based rolling only since we organize by date folders
                var roller = new RollingFileAppender
                {
                    Name = appenderName,
                    File = _logFilePath,
                    AppendToFile = true,
                    RollingStyle = RollingFileAppender.RollingMode.Size,
                    LockingModel = new FileAppender.MinimalLock(),
                    MaxSizeRollBackups = 5,
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

                // Add the appender and configure
                domainLogger.AddAppender(roller);
                domainLogger.Level = LogSettingsManager.GetLogLevel(domain);
                domainLogger.Additivity = false; // Don't propagate to root logger
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
