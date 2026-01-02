using System;

namespace ZerodhaDatafeedAdapter.Logging
{
    /// <summary>
    /// Unified logging interface for all components.
    /// Provides consistent logging API across the codebase.
    /// </summary>
    public interface ILoggerService
    {
        /// <summary>
        /// Gets whether debug logging is enabled.
        /// Use this to guard expensive string interpolation in hot paths.
        /// </summary>
        bool IsDebugEnabled { get; }

        /// <summary>
        /// Gets whether info logging is enabled.
        /// </summary>
        bool IsInfoEnabled { get; }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        void Debug(string message);

        /// <summary>
        /// Logs a debug message with exception.
        /// </summary>
        void Debug(string message, Exception exception);

        /// <summary>
        /// Logs an info message.
        /// </summary>
        void Info(string message);

        /// <summary>
        /// Logs an info message with exception.
        /// </summary>
        void Info(string message, Exception exception);

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        void Warn(string message);

        /// <summary>
        /// Logs a warning message with exception.
        /// </summary>
        void Warn(string message, Exception exception);

        /// <summary>
        /// Logs an error message.
        /// </summary>
        void Error(string message);

        /// <summary>
        /// Logs an error message with exception.
        /// </summary>
        void Error(string message, Exception exception);

        /// <summary>
        /// Logs a fatal message.
        /// </summary>
        void Fatal(string message);

        /// <summary>
        /// Logs a fatal message with exception.
        /// </summary>
        void Fatal(string message, Exception exception);

        /// <summary>
        /// Gets the path to the log file for this logger.
        /// </summary>
        string GetLogFilePath();
    }
}
