using System;
using System.IO;
using System.Text;
using ZerodhaDatafeedAdapter.Classes;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// CSV logger for synthetic straddle data to avoid cluttering NinjaTrader logs
    /// </summary>
    public static class SyntheticDataLogger
    {
        private static readonly object _logLock = new object();
        private static string _logDirectory;
        private static string _currentLogFile;
        private static DateTime _currentLogDate;

        // Debug mode controls - CSV logging only happens in debug mode
        private static bool _isDebugMode = false;

        static SyntheticDataLogger()
        {
            InitializeLogDirectory();
            
#if DEBUG
            _isDebugMode = true;
#else
            _isDebugMode = false;
#endif
        }

        /// <summary>
        /// Initialize the log directory
        /// </summary>
        private static void InitializeLogDirectory()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _logDirectory = Path.Combine(documentsPath, Constants.BaseDataFolder, "SyntheticLogs");
                
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"SyntheticDataLogger: Failed to initialize log directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the current log file path (creates new file daily)
        /// </summary>
        private static string GetCurrentLogFile()
        {
            DateTime today = DateTime.Now.Date;
            
            if (_currentLogFile == null || _currentLogDate != today)
            {
                _currentLogDate = today;
                string fileName = $"SyntheticData_{today:yyyyMMdd}.csv";
                _currentLogFile = Path.Combine(_logDirectory, fileName);
                
                if (!File.Exists(_currentLogFile))
                {
                    WriteHeader();
                }
            }
            
            return _currentLogFile;
        }

        /// <summary>
        /// Write CSV header
        /// </summary>
        private static void WriteHeader()
        {
            try
            {
                string header = "Timestamp,LogType,Symbol,Message,CEPrice,PEPrice,SyntheticPrice,Volume,CEBid,CEAsk,PEBid,PEAsk,SyntheticBid,SyntheticAsk";
                File.WriteAllText(_currentLogFile, header + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Error($"SyntheticDataLogger: Failed to write header: {ex.Message}");
            }
        }

        public static void LogSyntheticDetail(string syntheticSymbol, int recentCETicksCount, int recentPETicksCount, string currentTickSymbol, long currentTickVolume)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string message = $"Recent CE ticks: {recentCETicksCount}, Recent PE ticks: {recentPETicksCount}, Current tick: {currentTickSymbol}, Volume: {currentTickVolume}";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},SYNTHETIC-DETAIL,{syntheticSymbol},\"{message}\",,,,{currentTickVolume},,,,";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log synthetic detail: {ex.Message}");
                }
            }
        }

        public static void LogSyntheticVolume(string syntheticSymbol, long volume, string volumeSource)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string message = $"Using volume {volume} from {volumeSource}";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},SYNTHETIC-VOLUME,{syntheticSymbol},\"{message}\",,,,{volume},,,,";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log synthetic volume: {ex.Message}");
                }
            }
        }

        public static void LogTickAlignment(string syntheticSymbol, DateTime ceTimestamp, long ceVolume, DateTime peTimestamp, long peVolume, double timeDifferenceMs)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string message = $"Ticks aligned! CE: {ceTimestamp:HH:mm:ss.fff} ({ceVolume}), PE: {peTimestamp:HH:mm:ss.fff} ({peVolume}), Difference: {timeDifferenceMs:F2}ms";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},TICK-ALIGNMENT,{syntheticSymbol},\"{message}\",,,,{ceVolume + peVolume},,,,";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log tick alignment: {ex.Message}");
                }
            }
        }

        public static void LogSyntheticTick(string syntheticSymbol, double cePrice, double pePrice, double syntheticPrice, long volume,
            double ceBid = 0, double ceAsk = 0, double peBid = 0, double peAsk = 0, double syntheticBid = 0, double syntheticAsk = 0)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string message = $"Generated synthetic tick - Last: {syntheticPrice}, Bid: {syntheticBid}, Ask: {syntheticAsk}";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},SYNTHETIC-TICK,{syntheticSymbol},\"{message}\",{cePrice},{pePrice},{syntheticPrice},{volume},{ceBid},{ceAsk},{peBid},{peAsk},{syntheticBid},{syntheticAsk}";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log synthetic tick: {ex.Message}");
                }
            }
        }

        public static void LogLegTickProcessing(string instrumentSymbol, double price, long volume, int affectedStraddlesCount)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string message = $"Processing leg tick - Price: {price}, Volume: {volume}, Affected straddles: {affectedStraddlesCount}";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},LEG-TICK,{instrumentSymbol},\"{message}\",,,,{volume},,,,";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log leg tick processing: {ex.Message}");
                }
            }
        }

        public static void LogVolumeAnalysis(string syntheticSymbol, string volumeSource, bool foundMatch, long pendingCE, long pendingPE, long syntheticVolume)
        {
            if (!_isDebugMode) return;
            
            lock (_logLock)
            {
                try
                {
                    string logFile = GetCurrentLogFile();
                    string matchStatus = foundMatch ? "MATCHED" : "SINGLE";
                    string message = $"Volume Analysis - Source: {volumeSource}, Match: {matchStatus}, Pending CE: {pendingCE}, Pending PE: {pendingPE}, Final: {syntheticVolume}";
                    string csvLine = $"{DateTime.Now:HH:mm:ss.f},VOLUME-ANALYSIS,{syntheticSymbol},\"{message}\",,,,{syntheticVolume},,,,";
                    File.AppendAllText(logFile, csvLine + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Logger.Error($"SyntheticDataLogger: Failed to log volume analysis: {ex.Message}");
                }
            }
        }

        public static string GetLogDirectory()
        {
            return _logDirectory;
        }

        public static void SetDebugMode(bool enabled)
        {
            _isDebugMode = enabled;
            if (enabled)
            {
                Logger.Info("SyntheticDataLogger: CSV logging ENABLED");
            }
            else
            {
                Logger.Info("SyntheticDataLogger: CSV logging DISABLED");
            }
        }
        
        public static bool IsDebugMode => _isDebugMode;
    }
}
