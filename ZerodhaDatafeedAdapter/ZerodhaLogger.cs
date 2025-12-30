using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;

namespace ZerodhaDatafeedAdapter
{
    public static class Logger
    {
        private static readonly ILog _log;
        private static readonly string _logFolderPath;
        private static readonly string _logFilePath;
        private static bool _initialized = false;
        private static readonly object _lockObject = new object();

        static Logger()
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

                // Create log file name with date
                string fileName = $"ZerodhaAdapter_{DateTime.Now:yyyy-MM-dd}.log";
                _logFilePath = Path.Combine(_logFolderPath, fileName);

                // Try to load log4net.config from user's ZerodhaAdapter folder first
                string configPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "log4net.config");
                if (File.Exists(configPath))
                {
                    var configFile = new FileInfo(configPath);
                    XmlConfigurator.Configure(configFile);
                }
                else
                {
                    // Fall back to programmatic configuration
                    ConfigureLog4Net();
                }

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
            // Clear the log file on startup for a fresh session
            try
            {
                if (File.Exists(_logFilePath))
                {
                    File.WriteAllText(_logFilePath, string.Empty);
                }
            }
            catch
            {
                // Ignore errors clearing file - it may be locked
            }

            // Create a new configuration
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)LogManager.GetRepository();
            hierarchy.Root.RemoveAllAppenders(); // Remove any existing appenders

            // Create a rolling file appender
            var roller = new RollingFileAppender
            {
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
                ConversionPattern = "%date [%thread] %-5level %logger - %message%newline"
            };
            patternLayout.ActivateOptions();
            roller.Layout = patternLayout;

            // Activate the appender
            roller.ActivateOptions();
            hierarchy.Root.AddAppender(roller);

            // Set default logging level
            hierarchy.Root.Level = Level.Info;
            hierarchy.Configured = true;
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
                        // Load log4net configuration from file if exists
                        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        string configPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "log4net.config");

                        if (File.Exists(configPath))
                        {
                            var configFile = new FileInfo(configPath);
                            XmlConfigurator.Configure(configFile);
                        }
                        else
                        {
                            // Use default configuration
                            ConfigureLog4Net();
                        }

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
        /// Reloads configuration from the log4net.config file if it exists
        /// </summary>
        public static void ReloadConfiguration()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "log4net.config");

                if (File.Exists(configPath))
                {
                    var configFile = new FileInfo(configPath);
                    XmlConfigurator.Configure(configFile);
                    Info($"[Logger] Reloaded configuration from: {configPath}");
                }
                else
                {
                    Warn($"[Logger] Configuration file not found at: {configPath}");
                }
            }
            catch (Exception ex)
            {
                Error($"[Logger] Failed to reload configuration: {ex.Message}");
            }
        }
    }
}
