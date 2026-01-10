using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Log entry for the Terminal output.
    /// </summary>
    public class TerminalLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } // INFO, WARN, ERROR, DEBUG, SIGNAL, EVAL
        public string Message { get; set; }

        public string FormattedLine => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Message}";
    }

    /// <summary>
    /// Singleton service for terminal logging in the Option Signals module.
    /// Provides NinjaScript Output-style logging for strategy evaluation and signals.
    /// </summary>
    public class TerminalService
    {
        private static readonly Lazy<TerminalService> _instance =
            new Lazy<TerminalService>(() => new TerminalService());
        public static TerminalService Instance => _instance.Value;

        private readonly ObservableCollection<TerminalLogEntry> _logs = new ObservableCollection<TerminalLogEntry>();
        private Dispatcher _dispatcher;
        private const int MAX_LOG_ENTRIES = 1000;

        public ObservableCollection<TerminalLogEntry> Logs => _logs;

        private TerminalService()
        {
        }

        /// <summary>
        /// Sets the dispatcher for UI thread marshalling.
        /// </summary>
        public void SetDispatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Logs an info message.
        /// </summary>
        public void Info(string message)
        {
            Log("INFO", message);
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public void Warn(string message)
        {
            Log("WARN", message);
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        public void Error(string message)
        {
            Log("ERROR", message);
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public void Debug(string message)
        {
            Log("DEBUG", message);
        }

        /// <summary>
        /// Logs a signal event.
        /// </summary>
        public void Signal(string message)
        {
            Log("SIGNAL", message);
        }

        /// <summary>
        /// Logs a strategy evaluation event.
        /// </summary>
        public void Eval(string message)
        {
            Log("EVAL", message);
        }

        /// <summary>
        /// Logs an underlying bias update.
        /// </summary>
        public void Underlying(string message)
        {
            Log("INDEX", message);
        }

        private void Log(string level, string message)
        {
            var entry = new TerminalLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message
            };

            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.InvokeAsync(() => AddEntry(entry));
            }
            else
            {
                AddEntry(entry);
            }
        }

        private void AddEntry(TerminalLogEntry entry)
        {
            _logs.Add(entry);

            // Trim old entries
            while (_logs.Count > MAX_LOG_ENTRIES)
            {
                _logs.RemoveAt(0);
            }
        }

        /// <summary>
        /// Clears all log entries.
        /// </summary>
        public void Clear()
        {
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.InvokeAsync(() => _logs.Clear());
            }
            else
            {
                _logs.Clear();
            }
        }
    }
}
