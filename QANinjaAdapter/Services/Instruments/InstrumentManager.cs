using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Core.FloatingPoint;
using QABrokerAPI.Common.Enums;
using QANinjaAdapter.Classes;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Zerodha;

namespace QANinjaAdapter.Services.Instruments
{
    /// <summary>
    /// Manages instrument creation, mapping, and management
    /// </summary>
    public class InstrumentManager
    {
        private static InstrumentManager _instance;
        private readonly ZerodhaClient _zerodhaClient;
        
        // Cache for all instrument data and tokens
        private readonly Dictionary<string, long> _symbolToTokenMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, InstrumentDefinition> _tokenToInstrumentDataMap = new Dictionary<long, InstrumentDefinition>();
        
        private string _sqliteDbFullPath;
        private bool _isInitialized = false;
        private static readonly object _fileLock = new object();

        /// <summary>
        /// Gets the singleton instance of the InstrumentManager
        /// </summary>
        public static InstrumentManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new InstrumentManager();
                return _instance;
            }
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private InstrumentManager()
        {
            _zerodhaClient = ZerodhaClient.Instance;
            // Initialize asynchronously
            _ = InitializeAsync();
        }

        /// <summary>
        /// Unified initialization of instrument data from all sources
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                Logger.Info("InstrumentManager: Starting unified initialization...");

                // 1. Load static index mappings (never changes)
                LoadIndexMappings();

                // 2. Clear and prepare F&O mappings file (recreated on each startup)
                ClearFOMappings();

                // 3. Ensure SQLite database is ready
                await EnsureDatabaseExists();

                // 4. Load tokens into memory from SQLite (improves startup)
                LoadTokensFromSqlite();

                // 5. Optionally refresh tokens from API if memory cache is empty
                if (_symbolToTokenMap.Count < 10) // Threshold for "critically empty"
                {
                    await LoadInstrumentTokensFromApi();
                }

                _isInitialized = true;
                Logger.Info($"InstrumentManager: Initialization complete. Total symbols cached: {_symbolToTokenMap.Count}");
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Critical error during initialization: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads static index mappings (GIFT_NIFTY, NIFTY, SENSEX, BANKNIFTY) - these tokens never change
        /// </summary>
        private void LoadIndexMappings()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string jsonFilePath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.IndexMappingsFileName);

            if (!File.Exists(jsonFilePath))
            {
                Logger.Info($"InstrumentManager: No index mapping file found at {jsonFilePath}");
                return;
            }

            try
            {
                string jsonContent = File.ReadAllText(jsonFilePath);
                var instruments = JsonConvert.DeserializeObject<List<MappedInstrument>>(jsonContent);
                if (instruments != null)
                {
                    foreach (var instrument in instruments)
                    {
                        if (string.IsNullOrEmpty(instrument.symbol) || instrument.instrument_token <= 0) continue;

                        _symbolToTokenMap[instrument.symbol] = instrument.instrument_token;

                        var data = new InstrumentDefinition
                        {
                            Symbol = instrument.symbol,
                            BrokerSymbol = instrument.zerodhaSymbol,
                            Underlying = instrument.underlying,
                            Segment = instrument.segment,
                            InstrumentToken = instrument.instrument_token,
                            ExchangeToken = instrument.exchange_token ?? 0,
                            TickSize = instrument.tick_size,
                            LotSize = instrument.lot_size
                        };
                        _tokenToInstrumentDataMap[instrument.instrument_token] = data;

                        // Also map by zerodha symbol if different
                        if (!string.IsNullOrEmpty(instrument.zerodhaSymbol) &&
                            !instrument.zerodhaSymbol.Equals(instrument.symbol, StringComparison.OrdinalIgnoreCase))
                        {
                            _symbolToTokenMap[instrument.zerodhaSymbol] = instrument.instrument_token;
                        }
                    }
                    Logger.Info($"InstrumentManager: Loaded {instruments.Count} index mappings.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error loading index mappings: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the F&O mappings file on startup - it will be populated dynamically
        /// </summary>
        private void ClearFOMappings()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string jsonFilePath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.FOMappingsFileName);

                // Create empty array - will be populated as options are generated
                lock (_fileLock)
                {
                    File.WriteAllText(jsonFilePath, "[]");
                }
                Logger.Info("InstrumentManager: Cleared F&O mappings file for fresh session.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"InstrumentManager: Error clearing F&O mappings: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the instrument mapping by NT symbol name (e.g., "GIFT_NIFTY", "NIFTY 50", "SENSEX")
        /// </summary>
        public MappedInstrument GetMappingByNtSymbol(string ntSymbol)
        {
            if (string.IsNullOrEmpty(ntSymbol))
                return null;

            // First check if we have the token for this symbol
            if (_symbolToTokenMap.TryGetValue(ntSymbol, out long token))
            {
                // Then get the instrument data
                if (_tokenToInstrumentDataMap.TryGetValue(token, out InstrumentDefinition data))
                {
                    // Convert InstrumentDefinition to MappedInstrument
                    return new MappedInstrument
                    {
                        symbol = data.Symbol,
                        zerodhaSymbol = data.BrokerSymbol,
                        underlying = data.Underlying,
                        expiry = data.Expiry,
                        strike = data.Strike,
                        option_type = data.MarketType.ToString(),
                        segment = data.Segment,
                        instrument_token = data.InstrumentToken,
                        exchange_token = (int?)data.ExchangeToken,
                        tick_size = data.TickSize,
                        lot_size = data.LotSize
                    };
                }
            }

            Logger.Debug($"InstrumentManager.GetMappingByNtSymbol: No mapping found for '{ntSymbol}'");
            return null;
        }

        /// <summary>
        /// Adds a mapped instrument dynamically to memory and F&O mappings file
        /// </summary>
        public void AddMappedInstrument(MappedInstrument instrument)
        {
            try
            {
                if (instrument == null || string.IsNullOrEmpty(instrument.symbol)) return;

                // Update memory maps - cache both the NT symbol and the zerodha symbol
                _symbolToTokenMap[instrument.symbol] = instrument.instrument_token;

                // Also cache zerodhaSymbol if different from symbol (for API lookups)
                if (!string.IsNullOrEmpty(instrument.zerodhaSymbol) &&
                    !instrument.zerodhaSymbol.Equals(instrument.symbol, StringComparison.OrdinalIgnoreCase))
                {
                    _symbolToTokenMap[instrument.zerodhaSymbol] = instrument.instrument_token;
                }

                var data = new InstrumentDefinition {
                    Symbol = instrument.symbol,
                    BrokerSymbol = instrument.zerodhaSymbol,
                    Underlying = instrument.underlying,
                    Expiry = instrument.expiry,
                    Strike = instrument.strike,
                    MarketType = MarketType.Spot,
                    Segment = instrument.segment,
                    InstrumentToken = instrument.instrument_token,
                    ExchangeToken = (long)(instrument.exchange_token ?? 0),
                    TickSize = instrument.tick_size,
                    LotSize = instrument.lot_size
                };

                _tokenToInstrumentDataMap[instrument.instrument_token] = data;

                // Write to F&O mappings file (futures and options)
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string jsonFilePath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.FOMappingsFileName);

                lock (_fileLock)
                {
                    List<MappedInstrument> currentList = new List<MappedInstrument>();
                    if (File.Exists(jsonFilePath))
                    {
                        string content = File.ReadAllText(jsonFilePath);
                        currentList = JsonConvert.DeserializeObject<List<MappedInstrument>>(content) ?? new List<MappedInstrument>();
                    }

                    // Remove existing if any
                    currentList.RemoveAll(x => x.symbol == instrument.symbol);
                    currentList.Add(instrument);

                    File.WriteAllText(jsonFilePath, JsonConvert.SerializeObject(currentList, Formatting.Indented));
                }

            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error adding mapped instrument {instrument.symbol}. Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets unique expiry dates for a given underlying from the database
        /// </summary>
        public async Task<List<DateTime>> GetExpiriesForUnderlying(string underlying)
        {
            Logger.Info($"InstrumentManager: GetExpiriesForUnderlying({underlying}) called");

            // Ensure database path is set and database exists
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _sqliteDbFullPath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.InstrumentDbFileName);

            if (!File.Exists(_sqliteDbFullPath))
            {
                Logger.Info($"InstrumentManager: Database not found at {_sqliteDbFullPath}, downloading...");
                await DownloadAndCreateInstrumentDatabase();
            }

            var expiries = new List<DateTime>();
            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath))
            {
                Logger.Warn($"InstrumentManager: Database still not available after download attempt for expiry lookup of {underlying}");
                return expiries;
            }

            Logger.Info($"InstrumentManager: Querying database at {_sqliteDbFullPath} for {underlying} expiries");

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();
                    string query = "SELECT DISTINCT expiry FROM instruments WHERE underlying = @underlying AND expiry IS NOT NULL AND expiry != '' ORDER BY expiry";
                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@underlying", underlying);
                        Logger.Debug($"InstrumentManager: Executing query for underlying={underlying}");
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string dateStr = reader.GetString(0);
                                if (DateTime.TryParse(dateStr, out DateTime dt))
                                {
                                    expiries.Add(dt);
                                }
                            }
                        }
                    }
                }
                Logger.Info($"InstrumentManager: Found {expiries.Count} expiries for {underlying}");
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error fetching expiries for {underlying}: {ex.Message}");
            }
            return expiries;
        }

        

        /// <summary>
        /// Gets the instrument token for a symbol
        /// </summary>
        /// <param name="symbol">The symbol to get the token for</param>
        /// <returns>The instrument token</returns>
        public Task<long> GetInstrumentToken(string symbol)
        {
            try
            {
                // First try exact match in memory cache
                if (_symbolToTokenMap.TryGetValue(symbol, out long token))
                    return Task.FromResult(token);

                // If not found, try case-insensitive search in JSON mappings
                var match = _symbolToTokenMap.FirstOrDefault(kvp =>
                    string.Equals(kvp.Key, symbol, StringComparison.OrdinalIgnoreCase));

                if (!match.Equals(default(KeyValuePair<string, long>)))
                    return Task.FromResult(match.Value);

                // Check if this is a NIFTY futures symbol - map to NIFTY_I (continuous contract)
                // Matches patterns like NIFTY25MAYFUT, NIFTY25DECFUT, etc.
                string continuousSymbol = GetContinuousContractSymbol(symbol);
                if (!string.IsNullOrEmpty(continuousSymbol) && _symbolToTokenMap.TryGetValue(continuousSymbol, out token))
                {
                    Logger.Info($"InstrumentManager: Mapped '{symbol}' to continuous contract '{continuousSymbol}' with token {token}");
                    // Cache this mapping for future lookups
                    _symbolToTokenMap[symbol] = token;
                    return Task.FromResult(token);
                }

                // Fall back to SQLite database lookup
                token = LookupTokenInSqlite(symbol);
                if (token > 0)
                {
                    // Cache the result for future lookups
                    _symbolToTokenMap[symbol] = token;
                    return Task.FromResult(token);
                }

                throw new KeyNotFoundException($"Instrument token not found for symbol: {symbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error getting instrument token for symbol '{symbol}'. Error: {ex.Message}", ex);
                return Task.FromResult(0L);
            }
        }

        /// <summary>
        /// Gets the continuous contract symbol for a futures symbol
        /// E.g., NIFTY25MAYFUT -> NIFTY_I, BANKNIFTY25DECFUT -> BANKNIFTY_I
        /// </summary>
        private string GetContinuousContractSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return null;

            string upperSymbol = symbol.ToUpperInvariant();

            // Check for NIFTY futures (NIFTY25MAYFUT, NIFTY25DECFUT, etc.)
            if (upperSymbol.StartsWith("NIFTY") && upperSymbol.EndsWith("FUT") && !upperSymbol.StartsWith("BANKNIFTY"))
            {
                return "NIFTY_I";
            }

            // Check for BANKNIFTY futures
            if (upperSymbol.StartsWith("BANKNIFTY") && upperSymbol.EndsWith("FUT"))
            {
                return "BANKNIFTY_I";
            }

            // Check for SENSEX futures
            if (upperSymbol.StartsWith("SENSEX") && upperSymbol.EndsWith("FUT"))
            {
                return "SENSEX_I";
            }

            // Check for FINNIFTY futures
            if (upperSymbol.StartsWith("FINNIFTY") && upperSymbol.EndsWith("FUT"))
            {
                return "FINNIFTY_I";
            }

            // Check for MIDCPNIFTY futures
            if (upperSymbol.StartsWith("MIDCPNIFTY") && upperSymbol.EndsWith("FUT"))
            {
                return "MIDCPNIFTY_I";
            }

            return null;
        }

        /// <summary>
        /// Looks up instrument token from SQLite database by symbol name
        /// </summary>
        private long LookupTokenInSqlite(string symbol)
        {
            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath))
            {
                return 0;
            }

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();

                    // Try exact match on tradingsymbol first (this is the Zerodha trading symbol)
                    string query = "SELECT instrument_token FROM instruments WHERE tradingsymbol = @symbol LIMIT 1";
                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt64(result);
                        }
                    }

                    // Try case-insensitive match on tradingsymbol
                    query = "SELECT instrument_token FROM instruments WHERE tradingsymbol = @symbol COLLATE NOCASE LIMIT 1";
                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt64(result);
                        }
                    }

                    // Try match on underlying name (for indices like NIFTY, SENSEX)
                    query = "SELECT instrument_token FROM instruments WHERE name = @symbol COLLATE NOCASE AND segment IN ('NSE', 'BSE', 'INDICES') LIMIT 1";
                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt64(result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: SQLite lookup failed for symbol '{symbol}'. Error: {ex.Message}", ex);
            }

            return 0;
        }

        /// <summary>
        /// Looks up option instrument token from SQLite database by segment, underlying, expiry, strike, and option type
        /// </summary>
        /// <param name="segment">NFO-OPT for NIFTY options, BFO-OPT for SENSEX options</param>
        /// <param name="underlying">Underlying symbol (NIFTY, SENSEX)</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="strike">Strike price</param>
        /// <param name="optionType">CE or PE</param>
        /// <returns>Instrument token, or 0 if not found</returns>
        public long LookupOptionTokenInSqlite(string segment, string underlying, DateTime expiry, double strike, string optionType)
        {
            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath))
            {
                Logger.Warn($"InstrumentManager: SQLite DB not found for option lookup: {_sqliteDbFullPath}");
                return 0;
            }

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();

                    // Query by segment, underlying, expiry date, strike, and instrument_type (CE/PE)
                    // expiry in DB is stored as YYYY-MM-DD format
                    string expiryStr = expiry.ToString("yyyy-MM-dd");

                    string query = @"SELECT instrument_token, tradingsymbol FROM instruments
                                     WHERE segment = @segment
                                     AND underlying = @underlying
                                     AND expiry = @expiry
                                     AND strike = @strike
                                     AND instrument_type = @optionType
                                     LIMIT 1";

                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@segment", segment);
                        cmd.Parameters.AddWithValue("@underlying", underlying);
                        cmd.Parameters.AddWithValue("@expiry", expiryStr);
                        cmd.Parameters.AddWithValue("@strike", strike);
                        cmd.Parameters.AddWithValue("@optionType", optionType);

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                long token = reader.GetInt64(0);
                                string tradingSymbol = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                Logger.Info($"InstrumentManager: Found option token {token} (tradingsymbol={tradingSymbol}) for {underlying} {expiryStr} {strike} {optionType}");
                                return token;
                            }
                        }
                    }

                    Logger.Warn($"InstrumentManager: No option found in SQLite for {segment} {underlying} {expiryStr} {strike} {optionType}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: SQLite option lookup failed for {underlying} {expiry:yyyy-MM-dd} {strike} {optionType}. Error: {ex.Message}", ex);
            }

            return 0;
        }

        /// <summary>
        /// Looks up option instrument details from SQLite database by segment, underlying, expiry, strike, and option type
        /// Returns the tradingsymbol along with the token
        /// </summary>
        public (long token, string tradingSymbol) LookupOptionDetailsInSqlite(string segment, string underlying, DateTime expiry, double strike, string optionType)
        {
            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath))
            {
                return (0, null);
            }

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();

                    string expiryStr = expiry.ToString("yyyy-MM-dd");

                    string query = @"SELECT instrument_token, tradingsymbol FROM instruments
                                     WHERE segment = @segment
                                     AND underlying = @underlying
                                     AND expiry = @expiry
                                     AND strike = @strike
                                     AND instrument_type = @optionType
                                     LIMIT 1";

                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@segment", segment);
                        cmd.Parameters.AddWithValue("@underlying", underlying);
                        cmd.Parameters.AddWithValue("@expiry", expiryStr);
                        cmd.Parameters.AddWithValue("@strike", strike);
                        cmd.Parameters.AddWithValue("@optionType", optionType);

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                long token = reader.GetInt64(0);
                                string tradingSymbol = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                Logger.Info($"InstrumentManager: Found option {tradingSymbol} (token={token}) for {underlying} {expiryStr} {strike} {optionType}");
                                return (token, tradingSymbol);
                            }
                        }
                    }

                    Logger.Warn($"InstrumentManager: No option found in SQLite for {segment} {underlying} {expiryStr} {strike} {optionType}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: SQLite option detail lookup failed. Error: {ex.Message}", ex);
            }

            return (0, null);
        }

        /// <summary>
        /// Loads instrument tokens from the Zerodha API
        /// </summary>
        private async Task LoadInstrumentTokensFromApi()
        {
            try
            {
                // Only load if not already populated from JSON or DB
                if (_symbolToTokenMap.Count > 500) // Arbitrary threshold to check if broadly populated
                    return;

                Logger.Info("InstrumentManager: Broadening instrument cache from Zerodha API...");

                using (HttpClient client = _zerodhaClient.CreateAuthorizedClient())
                {
                    // Get all instruments
                    string url = "https://api.kite.trade/instruments";

                    HttpResponseMessage response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        string csvContent = await response.Content.ReadAsStringAsync();

                        // Parse CSV content
                        string[] lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                        if (lines.Length <= 1)
                        {
                            Logger.Warn("InstrumentManager: No instruments found in CSV from Zerodha API.");
                            return;
                        }

                        // Get column indices
                        string[] headers = lines[0].Split(',');
                        int tradingSymbolIndex = Array.IndexOf(headers, "tradingsymbol");
                        int instrumentTokenIndex = Array.IndexOf(headers, "instrument_token");
                        int exchangeIndex = Array.IndexOf(headers, "exchange");

                        if (tradingSymbolIndex < 0 || instrumentTokenIndex < 0 || exchangeIndex < 0)
                        {
                            Logger.Error("InstrumentManager: Required columns (tradingsymbol, instrument_token, exchange) not found in CSV from Zerodha API.");
                            return;
                        }

                        // Parse data rows
                        for (int i = 1; i < lines.Length; i++)
                        {
                            string[] fields = lines[i].Split(',');
                            if (fields.Length <= Math.Max(Math.Max(tradingSymbolIndex, instrumentTokenIndex), exchangeIndex))
                                continue;

                            string tradingSymbol = fields[tradingSymbolIndex];
                            string exchange = fields[exchangeIndex];
                            long instrumentToken;

                            if (long.TryParse(fields[instrumentTokenIndex], out instrumentToken))
                            {
                                // Use both exchange and symbol to create a unique key
                                string key = $"{exchange}:{tradingSymbol}";

                                if (!_symbolToTokenMap.ContainsKey(key))
                                {
                                    _symbolToTokenMap[key] = instrumentToken;
                                }

                                // Also add just the symbol for convenience
                                if (!_symbolToTokenMap.ContainsKey(tradingSymbol))
                                {
                                    _symbolToTokenMap[tradingSymbol] = instrumentToken;
                                }
                            }
                        }

                        Logger.Info($"InstrumentManager: Loaded {_symbolToTokenMap.Count} instrument tokens into cache");
                    }
                    else
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        Logger.Error($"InstrumentManager: Error loading instruments from Zerodha API. Status code: {response.StatusCode}, Error: {errorContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error loading instrument tokens from Zerodha API. Error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Downloads instruments from Zerodha API and creates/updates the SQLite database
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> DownloadAndCreateInstrumentDatabase()
        {
            try
            {
                Logger.Info("InstrumentManager: Downloading instruments from Zerodha API to create SQLite database...");

                using (HttpClient client = _zerodhaClient.CreateAuthorizedClient())
                {
                    string url = "https://api.kite.trade/instruments";
                    HttpResponseMessage response = await client.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        Logger.Error($"InstrumentManager: Failed to download instruments. Status: {response.StatusCode}, Error: {errorContent}");
                        return false;
                    }

                    string csvContent = await response.Content.ReadAsStringAsync();
                    string[] lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    if (lines.Length <= 1)
                    {
                        Logger.Warn("InstrumentManager: No instruments found in CSV.");
                        return false;
                    }

                    // Parse headers to get column indices
                    string[] headers = lines[0].Split(',');
                    var columnIndices = new Dictionary<string, int>();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        columnIndices[headers[i].Trim()] = i;
                    }

                    // Ensure required columns exist
                    string[] requiredColumns = { "instrument_token", "exchange_token", "tradingsymbol", "name",
                        "expiry", "strike", "tick_size", "lot_size", "instrument_type", "segment", "exchange" };
                    foreach (var col in requiredColumns)
                    {
                        if (!columnIndices.ContainsKey(col))
                        {
                            Logger.Error($"InstrumentManager: Required column '{col}' not found in CSV.");
                            return false;
                        }
                    }

                    // Ensure directory exists
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    string dbDir = Path.Combine(documentsPath, "NinjaTrader 8", "QAAdapter");
                    if (!Directory.Exists(dbDir))
                        Directory.CreateDirectory(dbDir);

                    _sqliteDbFullPath = Path.Combine(dbDir, "InstrumentMasters.db");

                    // Delete existing database to recreate fresh
                    if (File.Exists(_sqliteDbFullPath))
                    {
                        File.Delete(_sqliteDbFullPath);
                        Logger.Info("InstrumentManager: Deleted existing database for fresh download.");
                    }

                    // Create SQLite database
                    using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;"))
                    {
                        connection.Open();

                        // Create table
                        string createTableSql = @"
                            CREATE TABLE IF NOT EXISTS instruments (
                                instrument_token INTEGER PRIMARY KEY,
                                exchange_token INTEGER,
                                tradingsymbol TEXT,
                                name TEXT,
                                expiry TEXT,
                                strike REAL,
                                tick_size REAL,
                                lot_size INTEGER,
                                instrument_type TEXT,
                                segment TEXT,
                                exchange TEXT,
                                underlying TEXT
                            );
                            CREATE INDEX IF NOT EXISTS idx_tradingsymbol ON instruments(tradingsymbol);
                            CREATE INDEX IF NOT EXISTS idx_underlying ON instruments(underlying);
                            CREATE INDEX IF NOT EXISTS idx_expiry ON instruments(expiry);
                            CREATE INDEX IF NOT EXISTS idx_segment ON instruments(segment);
                        ";
                        using (var cmd = new SQLiteCommand(createTableSql, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Insert data using transaction for performance
                        using (var transaction = connection.BeginTransaction())
                        {
                            string insertSql = @"INSERT OR REPLACE INTO instruments
                                (instrument_token, exchange_token, tradingsymbol, name, expiry, strike,
                                 tick_size, lot_size, instrument_type, segment, exchange, underlying)
                                VALUES (@token, @exchToken, @symbol, @name, @expiry, @strike,
                                        @tickSize, @lotSize, @instrType, @segment, @exchange, @underlying)";

                            using (var cmd = new SQLiteCommand(insertSql, connection))
                            {
                                cmd.Parameters.Add("@token", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@exchToken", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                                cmd.Parameters.Add("@name", System.Data.DbType.String);
                                cmd.Parameters.Add("@expiry", System.Data.DbType.String);
                                cmd.Parameters.Add("@strike", System.Data.DbType.Double);
                                cmd.Parameters.Add("@tickSize", System.Data.DbType.Double);
                                cmd.Parameters.Add("@lotSize", System.Data.DbType.Int32);
                                cmd.Parameters.Add("@instrType", System.Data.DbType.String);
                                cmd.Parameters.Add("@segment", System.Data.DbType.String);
                                cmd.Parameters.Add("@exchange", System.Data.DbType.String);
                                cmd.Parameters.Add("@underlying", System.Data.DbType.String);

                                int insertedCount = 0;
                                for (int i = 1; i < lines.Length; i++)
                                {
                                    string[] fields = lines[i].Split(',');
                                    if (fields.Length < headers.Length)
                                        continue;

                                    try
                                    {
                                        cmd.Parameters["@token"].Value = long.Parse(fields[columnIndices["instrument_token"]]);
                                        cmd.Parameters["@exchToken"].Value = long.TryParse(fields[columnIndices["exchange_token"]], out long et) ? et : 0;
                                        cmd.Parameters["@symbol"].Value = fields[columnIndices["tradingsymbol"]];
                                        cmd.Parameters["@name"].Value = fields[columnIndices["name"]];
                                        cmd.Parameters["@expiry"].Value = fields[columnIndices["expiry"]];
                                        cmd.Parameters["@strike"].Value = double.TryParse(fields[columnIndices["strike"]], out double s) ? s : 0;
                                        cmd.Parameters["@tickSize"].Value = double.TryParse(fields[columnIndices["tick_size"]], out double ts) ? ts : 0.05;
                                        cmd.Parameters["@lotSize"].Value = int.TryParse(fields[columnIndices["lot_size"]], out int ls) ? ls : 1;
                                        cmd.Parameters["@instrType"].Value = fields[columnIndices["instrument_type"]];
                                        cmd.Parameters["@segment"].Value = fields[columnIndices["segment"]];
                                        cmd.Parameters["@exchange"].Value = fields[columnIndices["exchange"]];

                                        // Extract underlying from tradingsymbol for options/futures
                                        string tradingSymbol = fields[columnIndices["tradingsymbol"]];
                                        string instrType = fields[columnIndices["instrument_type"]];
                                        string underlying = ExtractUnderlying(tradingSymbol, instrType);
                                        cmd.Parameters["@underlying"].Value = underlying;

                                        cmd.ExecuteNonQuery();
                                        insertedCount++;

                                        if (insertedCount % 10000 == 0)
                                        {
                                            Logger.Info($"InstrumentManager: Inserted {insertedCount} instruments...");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Warn($"InstrumentManager: Error inserting row {insertedCount + 1}: {ex.Message}");
                                    }
                                }

                                transaction.Commit();
                                Logger.Info($"InstrumentManager: Successfully created SQLite database with {insertedCount} instruments at {_sqliteDbFullPath}");
                            }
                        }
                    }

                    Logger.Info($"InstrumentManager: Successfully created SQLite database at {_sqliteDbFullPath}");
                    _isInitialized = false; // Trigger reload
                    await InitializeAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error creating instrument database: {ex.Message}", ex);
                return false;
            }
        }

        private void LoadTokensFromSqlite()
        {
            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath)) return;

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();
                    string query = "SELECT tradingsymbol, instrument_token FROM instruments";
                    using (var cmd = new SQLiteCommand(query, connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        int count = 0;
                        while (reader.Read())
                        {
                            string symbol = reader.GetString(0);
                            long token = reader.GetInt64(1);
                            if (!_symbolToTokenMap.ContainsKey(symbol))
                            {
                                _symbolToTokenMap[symbol] = token;
                                count++;
                            }
                        }
                        Logger.Info($"InstrumentManager: Loaded {count} additional tokens from SQLite cache.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"InstrumentManager: Non-critical error loading tokens from SQLite: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts the underlying symbol from a trading symbol
        /// </summary>
        private string ExtractUnderlying(string tradingSymbol, string instrumentType)
        {
            if (string.IsNullOrEmpty(tradingSymbol))
                return "";

            // For options and futures, extract the underlying
            // NIFTY24DEC24500CE -> NIFTY
            // BANKNIFTY24DEC52000PE -> BANKNIFTY
            // SENSEX24D2780000CE -> SENSEX

            if (instrumentType == "CE" || instrumentType == "PE" ||
                instrumentType == "FUT" || instrumentType.Contains("OPT"))
            {
                // Find where the date/numbers start
                int i = 0;
                while (i < tradingSymbol.Length && !char.IsDigit(tradingSymbol[i]))
                {
                    i++;
                }
                if (i > 0)
                    return tradingSymbol.Substring(0, i);
            }

            return tradingSymbol;
        }

        /// <summary>
        /// Ensures the SQLite database exists, downloading if necessary
        /// </summary>
        public async Task EnsureDatabaseExists()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _sqliteDbFullPath = Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.InstrumentDbFileName);

            if (!File.Exists(_sqliteDbFullPath))
            {
                Logger.Info("InstrumentManager: SQLite database not found, downloading from Zerodha API...");
                await DownloadAndCreateInstrumentDatabase();
            }
            else
            {
                // Check if database is stale (older than 1 day)
                var fileInfo = new FileInfo(_sqliteDbFullPath);
                if (DateTime.Now - fileInfo.LastWriteTime > TimeSpan.FromDays(1))
                {
                    Logger.Info("InstrumentManager: SQLite database is older than 1 day, refreshing...");
                    await DownloadAndCreateInstrumentDatabase();
                }
            }
        }

        /// <summary>
        /// Looks up an option instrument token from the SQLite database
        /// </summary>
        public async Task<long> GetOptionToken(string underlying, DateTime expiry, double strike, string optionType)
        {
            await EnsureDatabaseExists();

            if (string.IsNullOrEmpty(_sqliteDbFullPath) || !File.Exists(_sqliteDbFullPath))
                return 0;

            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_sqliteDbFullPath};Version=3;Read Only=True;"))
                {
                    connection.Open();

                    // Format expiry as YYYY-MM-DD for comparison
                    string expiryStr = expiry.ToString("yyyy-MM-dd");

                    // Build the trading symbol pattern
                    // NIFTY24DEC24500CE format
                    string query = @"SELECT instrument_token FROM instruments
                                    WHERE underlying = @underlying
                                    AND expiry = @expiry
                                    AND strike = @strike
                                    AND instrument_type = @optType
                                    LIMIT 1";

                    using (var cmd = new SQLiteCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@underlying", underlying);
                        cmd.Parameters.AddWithValue("@expiry", expiryStr);
                        cmd.Parameters.AddWithValue("@strike", strike);
                        cmd.Parameters.AddWithValue("@optType", optionType);

                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt64(result);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"InstrumentManager: Error looking up option token: {ex.Message}", ex);
            }

            return 0;
        }

        /// <summary>
        /// Gets exchange information for all available instruments from both index and F&O mappings
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetBrokerInformation()
        {
            return await Task.Run(() =>
            {
                ObservableCollection<InstrumentDefinition> BrokerInformation = new ObservableCollection<InstrumentDefinition>();

                try
                {
                    string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    int count = 0;

                    // Load from both index and F&O mappings files
                    string[] jsonFiles = {
                        Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.IndexMappingsFileName),
                        Path.Combine(documentsPath, Constants.BaseDataFolder, Constants.FOMappingsFileName)
                    };

                    foreach (string jsonFilePath in jsonFiles)
                    {
                        if (!File.Exists(jsonFilePath))
                        {
                            Logger.Info($"Mapping file not found: {jsonFilePath}");
                            continue;
                        }

                        Logger.Info($"Reading symbols from JSON file: {jsonFilePath}");

                        string jsonContent = File.ReadAllText(jsonFilePath);
                        var mappedInstruments = JsonConvert.DeserializeObject<List<MappedInstrument>>(jsonContent);

                        if (mappedInstruments == null) continue;

                        Logger.Info($"Successfully read JSON file with {mappedInstruments.Count} instruments");

                        foreach (var instrument in mappedInstruments)
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(instrument.symbol))
                                    continue;

                                // Extract segment from the segment field (e.g., "NFO-FUT" -> "NFO")
                                string segment = instrument.segment?.Split('-')[0] ?? "NSE";

                                InstrumentDefinition instrumentDef = new InstrumentDefinition
                                {
                                    Symbol = instrument.symbol,
                                    BrokerSymbol = instrument.zerodhaSymbol ?? instrument.symbol,
                                    Segment = segment,
                                    TickSize = instrument.tick_size,
                                    LotSize = instrument.lot_size,
                                    Expiry = instrument.expiry,
                                    Strike = instrument.strike,
                                    Underlying = instrument.underlying,
                                    InstrumentToken = instrument.instrument_token,
                                    ExchangeToken = instrument.exchange_token
                                };

                                // Set market type based on segment
                                switch (segment.ToUpper())
                                {
                                    case "NSE":
                                    case "BSE":
                                    case "INDICES":
                                        instrumentDef.MarketType = MarketType.Spot;
                                        break;
                                    case "NFO":
                                    case "BFO":
                                        instrumentDef.MarketType = MarketType.UsdM;
                                        break;
                                    case "MCX":
                                        instrumentDef.MarketType = MarketType.Futures;
                                        break;
                                    case "CDS":
                                        instrumentDef.MarketType = MarketType.CoinM;
                                        break;
                                    default:
                                        instrumentDef.MarketType = MarketType.Spot;
                                        break;
                                }

                                _symbolToTokenMap[instrument.symbol] = instrument.instrument_token;

                                BrokerInformation.Add(instrumentDef);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error parsing symbol from JSON: {ex.Message}");
                            }
                        }
                    }

                    Logger.Info($"Successfully loaded {count} symbols from JSON files");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception in GetBrokerInformation: {ex.Message}");
                    MessageBox.Show($"Error loading symbols from JSON file: {ex.Message}",
                        "File Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return BrokerInformation;
            });
        }

        /// <summary>
        /// Registers Zerodha symbols in NinjaTrader
        /// </summary>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task RegisterSymbols()
        {
            var symbols = await GetBrokerInformation();
            int createdCount = 0;

            foreach (var symbol in symbols)
            {
                try
                {
                    string ntName;
                    symbol.QuoteAsset = "NSE";
                    bool success = CreateInstrument(symbol, out ntName);
                    if (success)
                    {
                        createdCount++;
                        Logger.Info($"✅ Created NT Instrument: {ntName}");
                    }
                }
                catch (Exception e)
                {
                    Logger.Error($"❌ Exception Occurred: {e.Message}");
                }
            }

            Logger.Info($"✅ Total symbols created: {createdCount}");
        }

        /// <summary>
        /// Gets the symbol name with market type
        /// </summary>
        /// <param name="symbol">The symbol</param>
        /// <param name="marketType">The market type</param>
        /// <returns>The symbol name</returns>
        public static string GetSymbolName(string symbol, out MarketType marketType)
        {
            marketType = MarketType.Spot;

            if (symbol == "NIFTY_I")
            {
                return "NIFTY25MAYFUT";
            }

            // Handle special index symbols - return the Zerodha symbol (with space, not underscore)
            // GIFT_NIFTY is the NT symbol, maps to "GIFT NIFTY" in Zerodha
            if (symbol == "GIFT_NIFTY")
            {
                return "GIFT NIFTY"; // Zerodha symbol has space, not underscore
            }

            // Detect NIFTY/BANKNIFTY options (NFO) - pattern: NIFTY{DATE}CE/PE or BANKNIFTY{DATE}CE/PE
            // Examples: NIFTY25DEC27650CE, NIFTY25DEC23500PE, BANKNIFTY25DEC52000CE
            if ((symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) ||
                 symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase) ||
                 symbol.StartsWith("FINNIFTY", StringComparison.OrdinalIgnoreCase) ||
                 symbol.StartsWith("MIDCPNIFTY", StringComparison.OrdinalIgnoreCase)) &&
                (symbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase) ||
                 symbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase)))
            {
                marketType = MarketType.UsdM; // NFO for NIFTY options
                return symbol;
            }

            // Detect SENSEX options (BFO) - pattern: SENSEX{DATE}CE/PE
            // Examples: SENSEX25DEC85400CE, SENSEX25DEC85400PE
            if (symbol.StartsWith("SENSEX", StringComparison.OrdinalIgnoreCase) &&
                (symbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase) ||
                 symbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase)))
            {
                marketType = MarketType.UsdM; // BFO for SENSEX options (use UsdM for F&O)
                return symbol;
            }

            string[] collection = symbol.Split('_');
            if (collection == null || collection.Length == 0)
                return "";
            if (collection.Length == 1 && collection[0].Contains("MCX")) marketType = MarketType.MCX;
            if (collection.Length == 2)
            {
                string str = "_" + collection[1].ToUpper();
                if (str.Contains("MCX"))
                    marketType = MarketType.MCX;
                if (str == "_NFO" || str == "_FNO")
                    marketType = MarketType.UsdM;
            }
            return collection[0];
        }

        /// <summary>
        /// Gets a valid name for the symbol with market type
        /// </summary>
        /// <param name="value">The symbol value</param>
        /// <param name="marketType">The market type</param>
        /// <returns>The valid name</returns>
        public string GetValidName(string value, MarketType marketType)
        {
            value = value.ToUpperInvariant();
            return value + GetSuffix(marketType);
        }

        /// <summary>
        /// Gets the suffix for a market type
        /// </summary>
        /// <param name="marketType">The market type</param>
        /// <returns>The suffix</returns>
        public static string GetSuffix(MarketType marketType)
        {
            switch (marketType)
            {
                case MarketType.Spot:
                    return ""; // Was "_NSE"
                case MarketType.UsdM:
                    return ""; // Was "_NFO"
                case MarketType.CoinM:
                    return ""; // Was "_MCX"
                default:
                    return string.Empty;
            }
        }

        public bool CreateInstrument(InstrumentDefinition instrument, out string ntSymbolName)
        {
            ntSymbolName = "";
            InstrumentType instrumentType = InstrumentType.Stock;
            string validName = instrument.Symbol;

            // Use the helper to determine and set trading hours (pass symbol for special handling)
            string templateName = NinjaTraderHelper.GetTradingHoursTemplate(instrument.Segment, validName);
            Logger.Info($"Setting up instrument '{validName}' with trading hours '{templateName}'");

            MasterInstrument masterInstrument1 = MasterInstrument.DbGet(validName, instrumentType);
            if (masterInstrument1 != null)
            {
                ntSymbolName = validName;
                if (DataContext.Instance.SymbolNames.ContainsKey(validName))
                    return false;
                    
                DataContext.Instance.SymbolNames.Add(validName, instrument.Symbol);
                int index = 1019;
                List<string> providerNames = new List<string>(masterInstrument1.ProviderNames);
                while (providerNames.Count <= index) providerNames.Add("");
                
                masterInstrument1.ProviderNames = providerNames.ToArray();
                masterInstrument1.ProviderNames[index] = instrument.Symbol;

                // Ensure trading hours are set
                if (masterInstrument1.TradingHours == null)
                {
                    NinjaTraderHelper.SetTradingHours(masterInstrument1, templateName);
                }

                masterInstrument1.DbUpdate();
                MasterInstrument.DbUpdateCache();
                return true;
            }

            double tickSize = instrument.TickSize > 0 ? instrument.TickSize : 0.05;

            // Create the MasterInstrument
            MasterInstrument masterInstrument2 = new MasterInstrument()
            {
                Description = instrument.Symbol,
                InstrumentType = instrumentType,
                Name = validName,
                PointValue = 1.0,
                TickSize = tickSize,
                Url = new Uri("https://kite.zerodha.com"),
                Exchanges = { Exchange.Default },
                Currency = Currency.IndianRupee
            };

            int providerIndex = 1019;
            var providers = new string[providerIndex + 1];
            for (int i = 0; i <= providerIndex; i++) providers[i] = "";
            providers[providerIndex] = instrument.Symbol;
            masterInstrument2.ProviderNames = providers;

            // Set trading hours via helper
            NinjaTraderHelper.SetTradingHours(masterInstrument2, templateName);
            
            masterInstrument2.DbAdd(false);

            new Instrument()
            {
                Exchange = Exchange.Default,
                MasterInstrument = masterInstrument2
            }.DbAdd();

            if (!DataContext.Instance.SymbolNames.ContainsKey(validName))
                DataContext.Instance.SymbolNames.Add(validName, instrument.Symbol);

            ntSymbolName = validName;
            return true;
        }

        /// <summary>
        /// Removes an instrument from NinjaTrader
        /// </summary>
        /// <param name="instrument">The instrument to remove</param>
        /// <returns>True if the instrument was removed successfully, false otherwise</returns>
        public bool RemoveInstrument(InstrumentDefinition instrument)
        {
            InstrumentType instrumentType = InstrumentType.Stock;
            string symbol = instrument.Symbol;
            MasterInstrument masterInstrument = MasterInstrument.DbGet(symbol, instrumentType);
            if (masterInstrument == null)
                return false;
            int index = 1019;
            if (masterInstrument.Url.AbsoluteUri == "https://kite.zerodha.com/")
                masterInstrument.DbRemove();
            else if (((IEnumerable<string>)masterInstrument.ProviderNames).ElementAtOrDefault<string>(index) != null)
            {
                masterInstrument.UserData = null;
                masterInstrument.ProviderNames[index] = "";
                masterInstrument.DbUpdate();
            }
            if (DataContext.Instance.SymbolNames.ContainsKey(symbol))
                DataContext.Instance.SymbolNames.Remove(symbol);
            return true;
        }

        /// <summary>
        /// Gets all NinjaTrader symbols
        /// </summary>
        /// <returns>A collection of instrument definitions</returns>
        public async Task<ObservableCollection<InstrumentDefinition>> GetNTSymbols()
        {
            return await Task.Run(() =>
            {
                ObservableCollection<InstrumentDefinition> ntSymbols = new ObservableCollection<InstrumentDefinition>();
                IEnumerable<MasterInstrument> source = MasterInstrument.All
                    .Where(x => !string.IsNullOrEmpty(x.ProviderNames.ElementAtOrDefault(1019)));

                foreach (MasterInstrument masterInstrument in source.OrderBy(x => x.Name).ToList())
                {
                    ntSymbols.Add(new InstrumentDefinition()
                    {
                        Symbol = masterInstrument.Name
                    });
                }

                return ntSymbols;
            });
        }

        /// <summary>
        /// Gets the segment for a given instrument token.
        /// </summary>
        /// <param name="token">The instrument token.</param>
        /// <returns>The segment string if found; otherwise, null.</returns>
        public string GetSegmentForToken(long token)
        {
            if (_isInitialized && _tokenToInstrumentDataMap.TryGetValue(token, out InstrumentDefinition data))
            {
                return data.Segment;
            }
            Logger.Warn($"InstrumentManager: Instrument data not found for token {token} when trying to get segment.");
            return null;
        }

    }
}
