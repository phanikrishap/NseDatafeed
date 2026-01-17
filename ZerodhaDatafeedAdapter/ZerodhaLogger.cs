using System;
using System.IO;
using System.Windows;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// Main application logger. Writes to the unified log folder structure.
    /// Log location: Documents\NinjaTrader 8\ZerodhaAdapter\Logs\{dd-MM-yyyy}\ZerodhaAdapter.log
    ///
    /// Log level can be configured via: Documents\NinjaTrader 8\ZerodhaAdapter\adapterLog-settings.json
    /// Example: { "LogLevel": "DEBUG" }
    /// </summary>
    public static class Logger
    {
        private static readonly ILog _log;
        private static readonly string _logFolderPath;
        private static readonly string _logFilePath;
        private static bool _initialized = false;
        private static readonly object _lockObject = new object();

        /// <summary>
        /// Default log level if no settings file exists.
        /// Now uses LogSettingsManager for centralized configuration.
        /// </summary>
        public const string DefaultLogLevel = "INFO"; // Kept for backwards compatibility

        static Logger()
        {
            try
            {
                // Use the unified LoggerFactory path (date-wise folder structure)
                _logFolderPath = LoggerFactory.LogFolderPath;

                // Create log file name (no date suffix - folder provides date context)
                string fileName = "ZerodhaAdapter.log";
                _logFilePath = Path.Combine(_logFolderPath, fileName);

                // Always use programmatic configuration for unified folder structure
                ConfigureLog4Net();

                // Get logger instance
                _log = LogManager.GetLogger(typeof(Logger));
                _initialized = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize logger: {ex.Message}",
                    "Logging Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void ConfigureLog4Net()
        {
            // Note: Log cleanup is handled by LoggerFactory on initialization
            // No need to clear files here as LoggerFactory.CleanupTodayLogs() handles it

            // Create a new configuration
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository();
            hierarchy.Root.RemoveAllAppenders(); // Remove any existing appenders

            // Create a rolling file appender (size-based only, date organization via folders)
            var roller = new RollingFileAppender
            {
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
                ConversionPattern = "%date [%thread] %-5level %logger - %message%newline"
            };
            patternLayout.ActivateOptions();
            roller.Layout = patternLayout;

            // Activate the appender
            roller.ActivateOptions();
            hierarchy.Root.AddAppender(roller);

            // Read log level from settings file or use default
            var logLevel = ReadLogLevelFromSettings();
            hierarchy.Root.Level = logLevel;
            hierarchy.Configured = true;
        }

        /// <summary>
        /// Reads the log level from adapterLog-settings.json file via LogSettingsManager.
        /// Uses the "Main" domain level or DefaultLogLevel.
        /// </summary>
        private static Level ReadLogLevelFromSettings()
        {
            // Use centralized LogSettingsManager for consistent configuration
            return LogSettingsManager.GetLogLevel(LogDomain.Main);
        }

        public static void Initialize()
        {
            // Ensure initialization happens only once
            lock (_lockObject)
            {
                if (!_initialized)
                {
                    try
                    {
                        // Always use programmatic configuration for unified folder structure
                        ConfigureLog4Net();

                        _initialized = true;
                        Info($"Logger initialized. Logs will be saved to: {_logFilePath}");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to initialize logger: {ex.Message}",
                            "Logging Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        #region Log methods

        /// <summary>
        /// Gets whether debug logging is enabled.
        /// Use this to guard expensive string interpolation in hot paths.
        /// </summary>
        public static bool IsDebugEnabled => _initialized && _log != null && _log.IsDebugEnabled;

        public static void Debug(string message)
        {
            if (_initialized && _log.IsDebugEnabled)
                _log.Debug(message);
        }

        public static void Debug(string message, Exception exception)
        {
            if (_initialized && _log.IsDebugEnabled)
                _log.Debug(message, exception);
        }

        public static void Info(string message)
        {
            if (_initialized && _log.IsInfoEnabled)
                _log.Info(message);
        }

        public static void Info(string message, Exception exception)
        {
            if (_initialized && _log.IsInfoEnabled)
                _log.Info(message, exception);
        }

        public static void Warn(string message)
        {
            if (_initialized && _log.IsWarnEnabled)
                _log.Warn(message);
        }

        public static void Warn(string message, Exception exception)
        {
            if (_initialized && _log.IsWarnEnabled)
                _log.Warn(message, exception);
        }

        public static void Error(string message)
        {
            if (_initialized)
                _log.Error(message);
        }

        public static void Error(string message, Exception exception)
        {
            if (_initialized)
                _log.Error(message, exception);
        }

        public static void Fatal(string message)
        {
            if (_initialized)
                _log.Fatal(message);
        }

        public static void Fatal(string message, Exception exception)
        {
            if (_initialized)
                _log.Fatal(message, exception);
        }

        #endregion

        /// <summary>
        /// Gets the full path to the current log file
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logFilePath;
        }

        /// <summary>
        /// Gets the directory where log files are stored
        /// </summary>
        public static string GetLogFolderPath()
        {
            return _logFolderPath;
        }

        /// <summary>
        /// Opens the log file in the default text editor
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
                    MessageBox.Show($"Log file not found at: {_logFilePath}",
                        "Log File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open log file: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the folder containing the log files
        /// </summary>
        public static void OpenLogFolder()
        {
            try
            {
                if (Directory.Exists(_logFolderPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", _logFolderPath);
                }
                else
                {
                    MessageBox.Show($"Log folder not found at: {_logFolderPath}",
                        "Log Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open log folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Sets the global log level at runtime
        /// </summary>
        /// <param name="level">The log level: "DEBUG", "INFO", "WARN", "ERROR", "FATAL"</param>
        public static void SetLogLevel(string level)
        {
            try
            {
                var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository();
                Level logLevel;

                switch (level.ToUpperInvariant())
                {
                    case "DEBUG":
                        logLevel = Level.Debug;
                        break;
                    case "INFO":
                        logLevel = Level.Info;
                        break;
                    case "WARN":
                    case "WARNING":
                        logLevel = Level.Warn;
                        break;
                    case "ERROR":
                        logLevel = Level.Error;
                        break;
                    case "FATAL":
                        logLevel = Level.Fatal;
                        break;
                    default:
                        logLevel = Level.Info;
                        break;
                }

                hierarchy.Root.Level = logLevel;
                Info($"[Logger] Log level changed to: {logLevel}");
            }
            catch (Exception ex)
            {
                Error($"[Logger] Failed to set log level: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current log level
        /// </summary>
        public static string GetLogLevel()
        {
            try
            {
                var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository();
                return hierarchy.Root.Level.Name;
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        /// <summary>
        /// Reloads log configuration from adapterLog-settings.json.
        /// Call this after modifying the settings file to apply changes without restart.
        /// </summary>
        public static void ReloadConfiguration()
        {
            try
            {
                // Force reload of settings
                LogSettingsManager.Reload();
                ConfigureLog4Net();
                Info($"[Logger] Configuration reloaded. Level={GetLogLevel()}, Logs at: {_logFilePath}");
            }
            catch (Exception ex)
            {
                Error($"[Logger] Failed to reload configuration: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path to the settings file.
        /// </summary>
        public static string GetSettingsFilePath() => LogSettingsManager.SettingsFilePath;

        /// <summary>
        /// Creates a default settings file if it doesn't exist.
        /// </summary>
        public static void CreateDefaultSettingsFile()
        {
            LogSettingsManager.CreateDefaultSettingsFile();
        }
    }
}
