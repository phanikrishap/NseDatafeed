using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Configuration;
using ZerodhaDatafeedAdapter.Services.Simulation;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// Rx-driven service for writing CSV reports from the OptionSignals module.
    /// Subscribes to SimulationStateStream to handle simulation date changes reactively.
    ///
    /// Generates CSV files:
    /// - Signals.csv: Faithful reproduction of the Signals tab
    /// - OptionsSignals.csv: ATM, ITM1, OTM1 strikes data for CE and PE
    /// - {Symbol}.csv: Individual strike files when WriteIndividualStrikes is enabled
    ///
    /// Files are written to: Documents\NinjaTrader 8\ZerodhaAdapter\CSVReports\{dd-MM-yyyy}\
    /// </summary>
    public class CsvReportService : IDisposable
    {
        private static readonly Lazy<CsvReportService> _instance =
            new Lazy<CsvReportService>(() => new CsvReportService());
        public static CsvReportService Instance => _instance.Value;

        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        private readonly object _signalsLock = new object();
        private readonly object _optionsLock = new object();

        private string _baseReportFolderPath;
        private string _todayReportFolderPath;
        private bool _initialized;

        // Rx subscriptions
        private CompositeDisposable _subscriptions;
        private bool _rxSubscribed;

        // CSV file names
        private const string SignalsCsvFileName = "Signals.csv";
        private const string OptionsSignalsCsvFileName = "OptionsSignals.csv";

        // Retention days (same as logs)
        public const int ReportRetentionDays = 5;

        // Track last written signal IDs to avoid duplicates
        private readonly HashSet<string> _writtenSignalIds = new HashSet<string>();

        // Track last minute boundary written for OptionsSignals
        private DateTime _lastOptionsWriteTime = DateTime.MinValue;

        // Track symbols that have qualified as ATM, ITM1, or OTM1 at any point
        // Once qualified, they are tracked continuously for individual strike CSV writing
        private readonly HashSet<string> _qualifiedSymbols = new HashSet<string>();
        private readonly object _qualifiedSymbolsLock = new object();

        // Track which individual strike files have been initialized with headers
        private readonly HashSet<string> _initializedStrikeFiles = new HashSet<string>();

        private CsvReportService()
        {
        }

        /// <summary>
        /// Gets whether CSV reporting is enabled in configuration.
        /// </summary>
        public bool IsEnabled => ConfigurationManager.Instance.CsvReportSettings?.WriteToCSV ?? false;

        /// <summary>
        /// Gets whether individual strike CSV writing is enabled in configuration.
        /// </summary>
        public bool IsIndividualStrikesEnabled => ConfigurationManager.Instance.CsvReportSettings?.WriteIndividualStrikes ?? false;

        /// <summary>
        /// Gets today's report folder path.
        /// Format: Documents\NinjaTrader 8\ZerodhaAdapter\CSVReports\{dd-MM-yyyy}\
        /// </summary>
        public string ReportFolderPath
        {
            get
            {
                EnsureInitialized();
                return _todayReportFolderPath;
            }
        }

        /// <summary>
        /// Initializes the service, creates folders, sets up Rx subscriptions.
        /// Called on adapter startup.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            // Get base folder path
            _baseReportFolderPath = Constants.GetFolderPath("CSVReports");

            // Initial folder path uses current date (will be updated reactively for simulation)
            _todayReportFolderPath = Path.Combine(_baseReportFolderPath, DateTime.Now.ToString("dd-MM-yyyy"));

            // Create base directory if needed
            if (!Directory.Exists(_baseReportFolderPath))
            {
                Directory.CreateDirectory(_baseReportFolderPath);
            }

            // Clean up stale reports (older than retention period)
            CleanupStaleReports();

            // Create today's folder
            if (!Directory.Exists(_todayReportFolderPath))
            {
                Directory.CreateDirectory(_todayReportFolderPath);
            }

            // Subscribe to simulation state changes
            SubscribeToSimulationState();

            _initialized = true;
            _log.Info($"[CsvReportService] Initialized. WriteToCSV={IsEnabled}, Folder={_todayReportFolderPath}");
        }

        /// <summary>
        /// Subscribes to SimulationStateStream for reactive state management.
        /// Uses BehaviorSubject pattern - late subscribers get current state immediately.
        /// </summary>
        private void SubscribeToSimulationState()
        {
            if (_rxSubscribed) return;

            _subscriptions = new CompositeDisposable();
            var hub = MarketDataReactiveHub.Instance;

            // Subscribe to simulation state changes
            // DistinctUntilChanged prevents redundant processing
            _subscriptions.Add(
                hub.SimulationStateStream
                    .DistinctUntilChanged(s => s.State)
                    .Subscribe(
                        state => OnSimulationStateChanged(state),
                        ex => _log.Error($"[CsvReportService] SimulationStateStream error: {ex.Message}")));

            _rxSubscribed = true;
            _log.Info("[CsvReportService] Subscribed to SimulationStateStream");
        }

        /// <summary>
        /// Handles simulation state changes reactively.
        /// </summary>
        private void OnSimulationStateChanged(SimulationStateUpdate state)
        {
            switch (state.State)
            {
                case SimulationState.Loading:
                    // Simulation starting - prepare for fresh data
                    _log.Info("[CsvReportService] Simulation loading - preparing for new session");
                    break;

                case SimulationState.Ready:
                    // Tick data loaded, simulation date is now available
                    // Reinitialize folder path for simulation date
                    ReinitializeForSimulationDate();
                    break;

                case SimulationState.Playing:
                    // Simulation is playing - CSV writes will happen normally
                    break;

                case SimulationState.Idle:
                case SimulationState.Completed:
                    // Simulation ended - reset to live mode date if needed
                    ResetToLiveDate();
                    break;
            }
        }

        /// <summary>
        /// Re-initializes the folder path for simulation mode.
        /// Called reactively when simulation state changes to Ready.
        /// </summary>
        private void ReinitializeForSimulationDate()
        {
            // Get simulation date directly from config (not SimulationTimeHelper.Today)
            // because at Ready state, playback hasn't started yet so CurrentSimTime is MinValue
            var simConfig = SimulationService.Instance.CurrentConfig;
            if (simConfig == null)
            {
                _log.Warn("[CsvReportService] ReinitializeForSimulationDate: No simulation config available");
                return;
            }

            DateTime simDate = simConfig.SimulationDate;
            string newFolderPath = Path.Combine(_baseReportFolderPath, simDate.ToString("dd-MM-yyyy"));

            _log.Info($"[CsvReportService] Reinitializing for simulation date {simDate:yyyy-MM-dd}");

            _todayReportFolderPath = newFolderPath;

            // Create folder if needed
            if (!Directory.Exists(_todayReportFolderPath))
            {
                Directory.CreateDirectory(_todayReportFolderPath);
            }

            // Clear tracking for fresh start
            ClearTrackingState();

            _log.Info($"[CsvReportService] Reinitialized. Folder={_todayReportFolderPath}");
        }

        /// <summary>
        /// Resets folder path to live date when simulation ends.
        /// </summary>
        private void ResetToLiveDate()
        {
            string liveFolderPath = Path.Combine(_baseReportFolderPath, DateTime.Now.ToString("dd-MM-yyyy"));

            if (liveFolderPath != _todayReportFolderPath)
            {
                _log.Info($"[CsvReportService] Resetting to live date folder");
                _todayReportFolderPath = liveFolderPath;

                // Create folder if needed
                if (!Directory.Exists(_todayReportFolderPath))
                {
                    Directory.CreateDirectory(_todayReportFolderPath);
                }

                // Clear tracking for fresh start
                ClearTrackingState();
            }
        }

        /// <summary>
        /// Clears internal tracking state for a fresh session.
        /// </summary>
        private void ClearTrackingState()
        {
            _writtenSignalIds.Clear();
            lock (_qualifiedSymbolsLock)
            {
                _qualifiedSymbols.Clear();
            }
            _initializedStrikeFiles.Clear();
            _lastOptionsWriteTime = DateTime.MinValue;
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Cleans up report folders older than the retention period.
        /// </summary>
        private void CleanupStaleReports()
        {
            try
            {
                if (!Directory.Exists(_baseReportFolderPath)) return;

                var cutoffDate = DateTime.Now.Date.AddDays(-(ReportRetentionDays - 1));
                var directories = Directory.GetDirectories(_baseReportFolderPath);

                foreach (var dir in directories)
                {
                    var folderName = Path.GetFileName(dir);
                    if (DateTime.TryParseExact(folderName, "dd-MM-yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime folderDate))
                    {
                        if (folderDate < cutoffDate)
                        {
                            try
                            {
                                Directory.Delete(dir, recursive: true);
                                _log.Info($"[CsvReportService] Deleted stale report folder: {folderName}");
                            }
                            catch
                            {
                                // Ignore - folder might be in use
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[CsvReportService] Error cleaning up stale reports: {ex.Message}");
            }
        }

        #region Signals.csv

        /// <summary>
        /// Writes or updates a signal to Signals.csv.
        /// Called when a signal is created, updated, or closed.
        /// </summary>
        public void WriteSignal(SignalRow signal)
        {
            if (!IsEnabled || signal == null) return;

            EnsureInitialized();

            lock (_signalsLock)
            {
                try
                {
                    string filePath = Path.Combine(_todayReportFolderPath, SignalsCsvFileName);
                    bool fileExists = File.Exists(filePath);

                    using (var writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8))
                    {
                        // Write header if new file
                        if (!fileExists)
                        {
                            writer.WriteLine(GetSignalsCsvHeader());
                        }

                        // Write signal row
                        writer.WriteLine(FormatSignalRow(signal));
                    }

                    _writtenSignalIds.Add(signal.SignalId);
                    _log.Debug($"[CsvReportService] Wrote signal: {signal.SignalId} Status={signal.Status}");
                }
                catch (Exception ex)
                {
                    _log.Error($"[CsvReportService] Error writing signal to CSV: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Rewrites the entire Signals.csv with current signal collection.
        /// Used for batch updates or corrections.
        /// </summary>
        public void RewriteAllSignals(IEnumerable<SignalRow> signals)
        {
            if (!IsEnabled || signals == null) return;

            EnsureInitialized();

            lock (_signalsLock)
            {
                try
                {
                    string filePath = Path.Combine(_todayReportFolderPath, SignalsCsvFileName);

                    using (var writer = new StreamWriter(filePath, append: false, encoding: Encoding.UTF8))
                    {
                        // Write header
                        writer.WriteLine(GetSignalsCsvHeader());

                        // Write all signals
                        foreach (var signal in signals)
                        {
                            writer.WriteLine(FormatSignalRow(signal));
                        }
                    }

                    _writtenSignalIds.Clear();
                    foreach (var signal in signals)
                    {
                        _writtenSignalIds.Add(signal.SignalId);
                    }

                    _log.Info($"[CsvReportService] Rewrote Signals.csv with {signals.Count()} signals");
                }
                catch (Exception ex)
                {
                    _log.Error($"[CsvReportService] Error rewriting Signals.csv: {ex.Message}");
                }
            }
        }

        private string GetSignalsCsvHeader()
        {
            return string.Join(",", new[]
            {
                "SignalId", "SignalTime", "Symbol", "Strike", "OptionType", "Moneyness",
                "Direction", "Status", "Quantity", "EntryPrice", "EntryTime", "CurrentPrice",
                "ExitPrice", "ExitTime", "UnrealizedPnL", "RealizedPnL", "StrategyName",
                "SignalReason", "DTE", "IsRealtime", "ExecutionTriggered", "BridgeOrderId",
                "BridgeRequestId", "BridgeError", "SessHvnB", "SessHvnS", "RollHvnB", "RollHvnS",
                "CDMomentum", "CDSmooth", "PriceMomentum", "PriceSmooth", "VwapScoreSess", "VwapScoreRoll"
            });
        }

        private string FormatSignalRow(SignalRow s)
        {
            return string.Join(",", new[]
            {
                EscapeCsv(s.SignalId),
                s.SignalTime.ToString("yyyy-MM-dd HH:mm:ss"),
                EscapeCsv(s.Symbol),
                s.Strike.ToString(CultureInfo.InvariantCulture),
                s.OptionType,
                s.Moneyness.ToString(),
                s.Direction.ToString(),
                s.Status.ToString(),
                s.Quantity.ToString(),
                s.EntryPrice.ToString("F2", CultureInfo.InvariantCulture),
                s.EntryTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                s.CurrentPrice.ToString("F2", CultureInfo.InvariantCulture),
                s.ExitPrice.ToString("F2", CultureInfo.InvariantCulture),
                s.ExitTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                s.UnrealizedPnL.ToString("F2", CultureInfo.InvariantCulture),
                s.RealizedPnL.ToString("F2", CultureInfo.InvariantCulture),
                EscapeCsv(s.StrategyName),
                EscapeCsv(s.SignalReason),
                s.DTE.ToString(),
                s.IsRealtime.ToString(),
                s.ExecutionTriggered.ToString(),
                s.BridgeOrderId.ToString(),
                s.BridgeRequestId.ToString(),
                EscapeCsv(s.BridgeError ?? ""),
                s.SessHvnB.ToString(),
                s.SessHvnS.ToString(),
                s.RollHvnB.ToString(),
                s.RollHvnS.ToString(),
                s.CDMomentum.ToString("F2", CultureInfo.InvariantCulture),
                s.CDSmooth.ToString("F2", CultureInfo.InvariantCulture),
                s.PriceMomentum.ToString("F2", CultureInfo.InvariantCulture),
                s.PriceSmooth.ToString("F2", CultureInfo.InvariantCulture),
                s.VwapScoreSess.ToString(),
                s.VwapScoreRoll.ToString()
            });
        }

        #endregion

        #region OptionsSignals.csv

        /// <summary>
        /// Writes OptionsSignals.csv row with ATM, ITM1, OTM1 data for CE and PE strikes.
        /// Called on ATR range bar close or 1-minute boundary.
        /// </summary>
        /// <param name="currentTime">Current time (system or simulation)</param>
        /// <param name="atmStrike">ATM strike price</param>
        /// <param name="strikeStep">Strike step size</param>
        /// <param name="rows">Dictionary of strike key to OptionSignalsRow</param>
        public void WriteOptionsSignalsRow(
            DateTime currentTime,
            double atmStrike,
            int strikeStep,
            IDictionary<string, OptionSignalsRow> rows)
        {
            if (!IsEnabled || rows == null || rows.Count == 0) return;

            EnsureInitialized();

            lock (_optionsLock)
            {
                try
                {
                    string filePath = Path.Combine(_todayReportFolderPath, OptionsSignalsCsvFileName);
                    bool fileExists = File.Exists(filePath);

                    // Get ATM, ITM1, OTM1 rows
                    var atmRow = GetRowForStrike(rows, (int)atmStrike);
                    var itm1CERow = GetRowForStrike(rows, (int)atmStrike - strikeStep); // ITM1 for CE
                    var otm1CERow = GetRowForStrike(rows, (int)atmStrike + strikeStep); // OTM1 for CE
                    // Note: ITM1 for PE is atmStrike + strikeStep, OTM1 for PE is atmStrike - strikeStep
                    // We use CE perspective for row naming, but include both CE and PE data

                    using (var writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8))
                    {
                        // Write header if new file
                        if (!fileExists)
                        {
                            writer.WriteLine(GetOptionsSignalsCsvHeader());
                        }

                        // Write row with all strike data
                        writer.WriteLine(FormatOptionsSignalsRow(currentTime, atmRow, itm1CERow, otm1CERow));
                    }

                    _lastOptionsWriteTime = currentTime;
                    _log.Debug($"[CsvReportService] Wrote OptionsSignals row at {currentTime:HH:mm:ss}");
                }
                catch (Exception ex)
                {
                    _log.Error($"[CsvReportService] Error writing OptionsSignals.csv: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Checks if we should write an OptionsSignals row based on time boundary.
        /// Returns true if on a 1-minute boundary or if enough time has passed.
        /// </summary>
        public bool ShouldWriteOptionsSignals(DateTime currentTime)
        {
            if (!IsEnabled) return false;

            // Write on 1-minute boundaries
            if (currentTime.Second == 0 &&
                (currentTime - _lastOptionsWriteTime).TotalSeconds >= 30)
            {
                return true;
            }

            return false;
        }

        private OptionSignalsRow GetRowForStrike(IDictionary<string, OptionSignalsRow> rows, int strike)
        {
            string key = $"STRIKE_{strike}";
            return rows.TryGetValue(key, out var row) ? row : null;
        }

        private string GetOptionsSignalsCsvHeader()
        {
            // Build comprehensive header for ATM, ITM1, OTM1 with CE and PE data
            var columns = new List<string> { "Time" };

            // ATM Strike data
            columns.AddRange(GetStrikeColumnHeaders("ATM"));

            // ITM1 Strike data (CE ITM1 = strike - step, PE ITM1 = strike + step)
            columns.AddRange(GetStrikeColumnHeaders("ITM1"));

            // OTM1 Strike data (CE OTM1 = strike + step, PE OTM1 = strike - step)
            columns.AddRange(GetStrikeColumnHeaders("OTM1"));

            return string.Join(",", columns);
        }

        private IEnumerable<string> GetStrikeColumnHeaders(string prefix)
        {
            return new[]
            {
                // Strike identifier
                $"{prefix}_Strike",

                // CE data
                $"{prefix}_CE_Time", $"{prefix}_CE_LTP", $"{prefix}_CE_AtrTime", $"{prefix}_CE_AtrClose",
                $"{prefix}_CE_HvnBSess", $"{prefix}_CE_HvnSSess", $"{prefix}_CE_TrendSess", $"{prefix}_CE_TrendSessTime",
                $"{prefix}_CE_HvnBRoll", $"{prefix}_CE_HvnSRoll", $"{prefix}_CE_TrendRoll", $"{prefix}_CE_TrendRollTime",
                $"{prefix}_CE_CDMomo", $"{prefix}_CE_CDSmooth", $"{prefix}_CE_PriceMomo", $"{prefix}_CE_PriceSmooth",
                $"{prefix}_CE_VwapScoreSess", $"{prefix}_CE_VwapScoreRoll",

                // PE data
                $"{prefix}_PE_Time", $"{prefix}_PE_LTP", $"{prefix}_PE_AtrTime", $"{prefix}_PE_AtrClose",
                $"{prefix}_PE_HvnBSess", $"{prefix}_PE_HvnSSess", $"{prefix}_PE_TrendSess", $"{prefix}_PE_TrendSessTime",
                $"{prefix}_PE_HvnBRoll", $"{prefix}_PE_HvnSRoll", $"{prefix}_PE_TrendRoll", $"{prefix}_PE_TrendRollTime",
                $"{prefix}_PE_CDMomo", $"{prefix}_PE_CDSmooth", $"{prefix}_PE_PriceMomo", $"{prefix}_PE_PriceSmooth",
                $"{prefix}_PE_VwapScoreSess", $"{prefix}_PE_VwapScoreRoll"
            };
        }

        private string FormatOptionsSignalsRow(DateTime time, OptionSignalsRow atmRow, OptionSignalsRow itm1Row, OptionSignalsRow otm1Row)
        {
            var values = new List<string> { time.ToString("yyyy-MM-dd HH:mm:ss") };

            // ATM data
            values.AddRange(FormatStrikeData(atmRow));

            // ITM1 data
            values.AddRange(FormatStrikeData(itm1Row));

            // OTM1 data
            values.AddRange(FormatStrikeData(otm1Row));

            return string.Join(",", values);
        }

        private IEnumerable<string> FormatStrikeData(OptionSignalsRow row)
        {
            if (row == null)
            {
                // Return empty values for missing strike (37 columns per strike)
                return Enumerable.Repeat("", 37);
            }

            return new[]
            {
                // Strike
                row.Strike.ToString(CultureInfo.InvariantCulture),

                // CE data
                row.CETickTime,
                row.CELTP,
                row.CEAtrTime,
                row.CEAtrLTP,
                row.CEHvnBSess,
                row.CEHvnSSess,
                row.CETrendSess.ToString(),
                row.CETrendSessTime,
                row.CEHvnBRoll,
                row.CEHvnSRoll,
                row.CETrendRoll.ToString(),
                row.CETrendRollTime,
                row.CECDMomo,
                row.CECDSmooth,
                row.CEPriceMomo,
                row.CEPriceSmooth,
                row.CEVwapScoreSess.ToString(),
                row.CEVwapScoreRoll.ToString(),

                // PE data
                row.PETickTime,
                row.PELTP,
                row.PEAtrTime,
                row.PEAtrLTP,
                row.PEHvnBSess,
                row.PEHvnSSess,
                row.PETrendSess.ToString(),
                row.PETrendSessTime,
                row.PEHvnBRoll,
                row.PEHvnSRoll,
                row.PETrendRoll.ToString(),
                row.PETrendRollTime,
                row.PECDMomo,
                row.PECDSmooth,
                row.PEPriceMomo,
                row.PEPriceSmooth,
                row.PEVwapScoreSess.ToString(),
                row.PEVwapScoreRoll.ToString()
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Escapes a string for CSV format (handles commas, quotes, newlines).
        /// </summary>
        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                // Escape quotes by doubling them and wrap in quotes
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        /// <summary>
        /// Gets the current time, accounting for simulation mode.
        /// </summary>
        public DateTime GetCurrentTime()
        {
            return SimulationTimeHelper.Now;
        }

        #endregion

        #region Individual Strike CSVs

        /// <summary>
        /// Registers a symbol as qualified (ATM, ITM1, or OTM1).
        /// Once registered, the symbol will be tracked continuously for individual strike CSV writing.
        /// </summary>
        /// <param name="symbol">The instrument symbol (e.g., NIFTY25JAN23500CE)</param>
        public void RegisterQualifiedSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol) || !IsIndividualStrikesEnabled) return;

            lock (_qualifiedSymbolsLock)
            {
                if (_qualifiedSymbols.Add(symbol))
                {
                    _log.Info($"[CsvReportService] Registered qualified symbol for tracking: {symbol}");
                }
            }
        }

        /// <summary>
        /// Checks if a symbol is registered as qualified.
        /// </summary>
        public bool IsSymbolQualified(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;

            lock (_qualifiedSymbolsLock)
            {
                return _qualifiedSymbols.Contains(symbol);
            }
        }

        /// <summary>
        /// Writes a row to an individual strike CSV file.
        /// Called on every bar close for qualified symbols.
        /// </summary>
        /// <param name="symbol">The instrument symbol (used as filename)</param>
        /// <param name="currentTime">Current time (system or simulation)</param>
        /// <param name="optionType">CE or PE</param>
        /// <param name="row">The OptionSignalsRow containing current metrics</param>
        public void WriteIndividualStrikeRow(string symbol, DateTime currentTime, string optionType, OptionSignalsRow row)
        {
            if (!IsIndividualStrikesEnabled || string.IsNullOrEmpty(symbol) || row == null) return;

            // Check if this symbol is qualified for tracking
            if (!IsSymbolQualified(symbol)) return;

            EnsureInitialized();

            try
            {
                // Create filename from symbol (e.g., NIFTY25JAN23500CE.csv)
                string fileName = $"{symbol}.csv";
                string filePath = Path.Combine(_todayReportFolderPath, fileName);

                bool needsHeader = !_initializedStrikeFiles.Contains(symbol) && !File.Exists(filePath);

                using (var writer = new StreamWriter(filePath, append: true, encoding: Encoding.UTF8))
                {
                    // Write header if new file
                    if (needsHeader)
                    {
                        writer.WriteLine(GetIndividualStrikeCsvHeader());
                        _initializedStrikeFiles.Add(symbol);
                    }

                    // Write data row
                    writer.WriteLine(FormatIndividualStrikeRow(currentTime, optionType, row));
                }

                _log.Debug($"[CsvReportService] Wrote individual strike row: {symbol} at {currentTime:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _log.Error($"[CsvReportService] Error writing individual strike CSV for {symbol}: {ex.Message}");
            }
        }

        private string GetIndividualStrikeCsvHeader()
        {
            return string.Join(",", new[]
            {
                "Time", "Strike", "OptionType",
                "TickTime", "LTP", "AtrTime", "AtrClose",
                "HvnBSess", "HvnSSess", "TrendSess", "TrendSessTime",
                "HvnBRoll", "HvnSRoll", "TrendRoll", "TrendRollTime",
                "CDMomo", "CDSmooth", "PriceMomo", "PriceSmooth",
                "VwapScoreSess", "VwapScoreRoll"
            });
        }

        private string FormatIndividualStrikeRow(DateTime time, string optionType, OptionSignalsRow row)
        {
            // Extract data based on option type (CE or PE)
            if (optionType == "CE")
            {
                return string.Join(",", new[]
                {
                    time.ToString("yyyy-MM-dd HH:mm:ss"),
                    row.Strike.ToString(CultureInfo.InvariantCulture),
                    "CE",
                    row.CETickTime ?? "",
                    row.CELTP ?? "",
                    row.CEAtrTime ?? "",
                    row.CEAtrLTP ?? "",
                    row.CEHvnBSess ?? "0",
                    row.CEHvnSSess ?? "0",
                    row.CETrendSess.ToString(),
                    row.CETrendSessTime ?? "",
                    row.CEHvnBRoll ?? "0",
                    row.CEHvnSRoll ?? "0",
                    row.CETrendRoll.ToString(),
                    row.CETrendRollTime ?? "",
                    row.CECDMomo ?? "0",
                    row.CECDSmooth ?? "0",
                    row.CEPriceMomo ?? "0",
                    row.CEPriceSmooth ?? "0",
                    row.CEVwapScoreSess.ToString(),
                    row.CEVwapScoreRoll.ToString()
                });
            }
            else // PE
            {
                return string.Join(",", new[]
                {
                    time.ToString("yyyy-MM-dd HH:mm:ss"),
                    row.Strike.ToString(CultureInfo.InvariantCulture),
                    "PE",
                    row.PETickTime ?? "",
                    row.PELTP ?? "",
                    row.PEAtrTime ?? "",
                    row.PEAtrLTP ?? "",
                    row.PEHvnBSess ?? "0",
                    row.PEHvnSSess ?? "0",
                    row.PETrendSess.ToString(),
                    row.PETrendSessTime ?? "",
                    row.PEHvnBRoll ?? "0",
                    row.PEHvnSRoll ?? "0",
                    row.PETrendRoll.ToString(),
                    row.PETrendRollTime ?? "",
                    row.PECDMomo ?? "0",
                    row.PECDSmooth ?? "0",
                    row.PEPriceMomo ?? "0",
                    row.PEPriceSmooth ?? "0",
                    row.PEVwapScoreSess.ToString(),
                    row.PEVwapScoreRoll.ToString()
                });
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _subscriptions?.Dispose();
            _subscriptions = null;
            _rxSubscribed = false;
        }

        #endregion
    }
}
