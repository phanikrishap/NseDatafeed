using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using ExcelDataReader;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services
{
    /// <summary>
    /// Service for reading and managing TBS configurations from Excel file
    /// </summary>
    public class TBSConfigurationService
    {
        private static TBSConfigurationService _instance;
        private static readonly object _lock = new object();

        private List<TBSConfigEntry> _allConfigs = new List<TBSConfigEntry>();
        private DateTime _lastLoadTime = DateTime.MinValue;
        private string _configFilePath;

        public static TBSConfigurationService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new TBSConfigurationService();
                    }
                }
                return _instance;
            }
        }

        private TBSConfigurationService()
        {
            _configFilePath = Classes.Constants.GetFolderPath("tbsConfig.xlsx");
        }

        /// <summary>
        /// Gets the path to the config file
        /// </summary>
        public string ConfigFilePath => _configFilePath;

        /// <summary>
        /// Load all configurations from the Excel file
        /// </summary>
        public List<TBSConfigEntry> LoadConfigurations(bool forceReload = false)
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    Logger.Warn($"[TBSConfig] Config file not found: {_configFilePath}");
                    return new List<TBSConfigEntry>();
                }

                // Check if we need to reload
                var fileInfo = new FileInfo(_configFilePath);
                if (!forceReload && _allConfigs.Count > 0 && fileInfo.LastWriteTime <= _lastLoadTime)
                {
                    return _allConfigs;
                }

                _allConfigs.Clear();

                // ExcelDataReader requires code page encoding to be registered
                // For .NET Framework 4.8, this is not needed as code pages are already available

                using (var stream = File.Open(_configFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration
                    {
                        ConfigureDataTable = _ => new ExcelDataTableConfiguration
                        {
                            UseHeaderRow = true
                        }
                    });

                    if (result.Tables.Count == 0)
                    {
                        Logger.Warn("[TBSConfig] No sheets found in Excel file");
                        return _allConfigs;
                    }

                    var table = result.Tables[0];
                    Logger.Info($"[TBSConfig] Loading {table.Rows.Count} rows from {table.TableName}");

                    foreach (DataRow row in table.Rows)
                    {
                        try
                        {
                            var config = ParseRow(row, table.Columns);
                            if (config != null)
                            {
                                _allConfigs.Add(config);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[TBSConfig] Error parsing row: {ex.Message}");
                        }
                    }
                }

                _lastLoadTime = DateTime.Now;
                Logger.Info($"[TBSConfig] Loaded {_allConfigs.Count} configurations");
                return _allConfigs;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TBSConfig] Error loading configurations: {ex.Message}", ex);
                return new List<TBSConfigEntry>();
            }
        }

        /// <summary>
        /// Parse a DataRow into a TBSConfigEntry
        /// </summary>
        private TBSConfigEntry ParseRow(DataRow row, DataColumnCollection columns)
        {
            var config = new TBSConfigEntry();

            // Track SL type to determine if SL% goes to Individual or Combined
            string slType = null;
            decimal slPercent = 0;

            // Map columns flexibly (case-insensitive, various naming conventions)
            foreach (DataColumn col in columns)
            {
                // Normalize column name: lowercase, remove underscores, spaces, and special chars like %
                var colName = col.ColumnName.ToLowerInvariant()
                    .Replace("_", "")
                    .Replace(" ", "")
                    .Replace("%", "");
                var value = row[col];

                if (value == null || value == DBNull.Value)
                    continue;

                var strValue = value.ToString().Trim();

                switch (colName)
                {
                    case "underlying":
                    case "symbol":
                    case "index":
                        config.Underlying = strValue.ToUpperInvariant();
                        break;

                    case "dte":
                    case "daystoexpiry":
                        if (int.TryParse(strValue, out int dte))
                            config.DTE = dte;
                        break;

                    case "entrytime":
                    case "entry":
                        config.EntryTime = ParseTimeSpan(strValue);
                        break;

                    case "exittime":
                    case "exit":
                        config.ExitTime = ParseTimeSpan(strValue);
                        break;

                    // SL% column - store temporarily, will assign based on SL Type
                    case "sl":
                    case "slpercent":
                    case "stoploss":
                        slPercent = ParseDecimalPercent(strValue);
                        break;

                    // SL Type determines if SL% is individual or combined
                    case "sltype":
                    case "slmode":
                    case "stoptype":
                        slType = strValue.ToLowerInvariant().Replace("_", "").Replace(" ", "");
                        break;

                    case "individualsl":
                    case "legsl":
                    case "indsl":
                        config.IndividualSL = ParseDecimalPercent(strValue);
                        break;

                    case "combinedsl":
                    case "totalsl":
                    case "combsl":
                        config.CombinedSL = ParseDecimalPercent(strValue);
                        break;

                    case "tgt":
                    case "target":
                    case "targetpercent":
                    case "targetprofit":
                    case "profittarget":
                        config.TargetPercent = ParseDecimalPercent(strValue);
                        break;

                    case "hedgeaction":
                    case "action":
                    case "slaction":
                        config.HedgeAction = strValue.ToLowerInvariant().Replace(" ", "_");
                        break;

                    case "quantity":
                    case "qty":
                    case "lots":
                        if (int.TryParse(strValue, out int qty))
                            config.Quantity = qty;
                        break;

                    case "active":
                    case "enabled":
                    case "isactive":
                        config.IsActive = strValue.ToLowerInvariant() == "true" ||
                                         strValue.ToLowerInvariant() == "yes" ||
                                         strValue == "1";
                        break;

                    case "profitcondition":
                    case "profitcond":
                    case "profcondition":
                    case "profcond":
                        // Blank or "false" = false, "true" = true
                        config.ProfitCondition = strValue.ToLowerInvariant() == "true" ||
                                                strValue.ToLowerInvariant() == "yes" ||
                                                strValue == "1";
                        break;
                }
            }

            // Assign SL% based on SL Type (if not already set via specific columns)
            if (slPercent > 0)
            {
                if (slType != null && (slType.Contains("individual") || slType.Contains("leg") || slType.Contains("perleg")))
                {
                    // Individual/per-leg SL
                    if (config.IndividualSL == 0)
                        config.IndividualSL = slPercent;
                }
                else if (slType != null && (slType.Contains("combined") || slType.Contains("total") || slType.Contains("comb")))
                {
                    // Combined SL
                    if (config.CombinedSL == 0)
                        config.CombinedSL = slPercent;
                }
                else
                {
                    // Default to individual SL if type not specified or unrecognized
                    if (config.IndividualSL == 0)
                        config.IndividualSL = slPercent;
                }
                Logger.Debug($"[TBSConfig] SL% assigned: Type='{slType}', Value={slPercent:P0}, IndSL={config.IndividualSL:P0}, CombSL={config.CombinedSL:P0}");
            }

            // Validate required fields
            if (string.IsNullOrEmpty(config.Underlying))
            {
                Logger.Debug("[TBSConfig] Skipping row: missing underlying");
                return null;
            }

            // Set defaults for optional fields
            if (config.Quantity <= 0)
                config.Quantity = 1;

            if (string.IsNullOrEmpty(config.HedgeAction))
                config.HedgeAction = "exit_both";

            return config;
        }

        /// <summary>
        /// Parse time string to TimeSpan (supports HH:mm, HH:mm:ss, or Excel time format)
        /// </summary>
        private TimeSpan ParseTimeSpan(string value)
        {
            if (string.IsNullOrEmpty(value))
                return TimeSpan.Zero;

            // Try parsing as TimeSpan
            if (TimeSpan.TryParse(value, out TimeSpan ts))
                return ts;

            // Try parsing as DateTime and extract time
            if (DateTime.TryParse(value, out DateTime dt))
                return dt.TimeOfDay;

            // Try parsing as decimal (Excel stores times as fraction of day)
            if (double.TryParse(value, out double excelTime))
            {
                return TimeSpan.FromDays(excelTime);
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// Parse a percentage value (handles "50%", "0.5", "50")
        /// </summary>
        private decimal ParseDecimalPercent(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            value = value.Trim().TrimEnd('%');

            if (decimal.TryParse(value, out decimal result))
            {
                // If value is > 1, assume it's a percentage and divide by 100
                if (result > 1)
                    return result / 100m;
                return result;
            }

            return 0;
        }

        /// <summary>
        /// Get configurations filtered by underlying and DTE
        /// </summary>
        public List<TBSConfigEntry> GetConfigurations(string underlying = null, int? dte = null)
        {
            var configs = LoadConfigurations();

            var filtered = configs.Where(c => c.IsActive);

            if (!string.IsNullOrEmpty(underlying))
            {
                filtered = filtered.Where(c =>
                    c.Underlying.Equals(underlying, StringComparison.OrdinalIgnoreCase));
            }

            if (dte.HasValue)
            {
                filtered = filtered.Where(c => c.DTE == dte.Value);
            }

            return filtered.ToList();
        }

        /// <summary>
        /// Get configuration that matches today's trading conditions
        /// </summary>
        public TBSConfigEntry GetActiveConfigForToday(string underlying, DateTime expiry)
        {
            int dte = (int)(expiry.Date - DateTime.Today).TotalDays;
            var configs = GetConfigurations(underlying, dte);
            return configs.FirstOrDefault();
        }

        /// <summary>
        /// Get all unique underlying symbols in the configuration
        /// </summary>
        public List<string> GetUnderlyingList()
        {
            return LoadConfigurations()
                .Select(c => c.Underlying)
                .Distinct()
                .OrderBy(u => u)
                .ToList();
        }

        /// <summary>
        /// Get all unique DTE values in the configuration
        /// </summary>
        public List<int> GetDTEList()
        {
            return LoadConfigurations()
                .Select(c => c.DTE)
                .Distinct()
                .OrderBy(d => d)
                .ToList();
        }

        /// <summary>
        /// Reload configurations from file
        /// </summary>
        public void Reload()
        {
            LoadConfigurations(forceReload: true);
        }
    }
}
