using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Zerodha;

namespace ZerodhaDatafeedAdapter.Services.Instruments
{
    /// <summary>
    /// Specialized service for managing the SQLite instrument database.
    /// Handles downloads, creation, and token lookups.
    /// Aligned with original InstrumentManager SQLite query patterns.
    /// </summary>
    public class InstrumentDbService
    {
        private readonly string _dbPath;
        private readonly ZerodhaClient _zerodhaClient;

        // In-memory cache for fast token lookups
        private readonly ConcurrentDictionary<string, long> _symbolToTokenCache = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private bool _cacheLoaded = false;

        public InstrumentDbService(string dbPath)
        {
            _dbPath = dbPath;
            _zerodhaClient = ZerodhaClient.Instance;
        }

        /// <summary>
        /// Loads all tokens from SQLite into memory cache for fast lookups.
        /// Call this during initialization.
        /// </summary>
        public void LoadAllTokensToCache()
        {
            if (_cacheLoaded || string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("SELECT tradingsymbol, instrument_token FROM instruments WHERE tradingsymbol IS NOT NULL", conn))
                    {
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string symbol = reader.GetString(0);
                                long token = reader.GetInt64(1);
                                if (!string.IsNullOrEmpty(symbol))
                                {
                                    _symbolToTokenCache[symbol] = token;
                                }
                            }
                        }
                    }
                }
                _cacheLoaded = true;
                Logger.Info($"[IDB] Loaded {_symbolToTokenCache.Count} tokens into memory cache.");
            }
            catch (Exception ex)
            {
                Logger.Error("[IDB] Error loading tokens to cache:", ex);
            }
        }

        public async Task<bool> DownloadAndCreateInstrumentDatabaseAsync()
        {
            Logger.Info("[IDB] Downloading instruments from Zerodha...");
            try
            {
                using (var client = new HttpClient())
                {
                    var csv = await client.GetStringAsync("https://api.kite.trade/instruments");
                    if (string.IsNullOrEmpty(csv)) return false;

                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(_dbPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;"))
                    {
                        await conn.OpenAsync();
                        using (var transaction = conn.BeginTransaction())
                        {
                            // Drop existing table and recreate with correct schema including underlying column
                            string dropTable = "DROP TABLE IF EXISTS instruments";
                            using (var cmd = new SQLiteCommand(dropTable, conn)) await cmd.ExecuteNonQueryAsync();

                            // Create table with underlying column (matches actual database schema)
                            string createTable = @"CREATE TABLE instruments (
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
                            )";
                            using (var cmd = new SQLiteCommand(createTable, conn)) await cmd.ExecuteNonQueryAsync();

                            // Create indexes for fast lookups
                            string[] indexes = {
                                "CREATE INDEX idx_tradingsymbol ON instruments(tradingsymbol)",
                                "CREATE INDEX idx_underlying ON instruments(underlying)",
                                "CREATE INDEX idx_expiry ON instruments(expiry)",
                                "CREATE INDEX idx_segment ON instruments(segment)"
                            };
                            foreach (var idx in indexes)
                            {
                                using (var cmd = new SQLiteCommand(idx, conn)) await cmd.ExecuteNonQueryAsync();
                            }

                            // Insert new data (Bulk insert)
                            // CSV columns: instrument_token,exchange_token,tradingsymbol,name,last_price,expiry,strike,tick_size,lot_size,instrument_type,segment,exchange
                            // Note: underlying is derived from name for F&O instruments
                            string insertSql = @"INSERT INTO instruments (instrument_token, exchange_token, tradingsymbol, name, expiry, strike, tick_size, lot_size, instrument_type, segment, exchange, underlying)
                                               VALUES (@token, @etoken, @symbol, @name, @expiry, @strike, @tick, @lot, @type, @segment, @exchange, @underlying)";

                            using (var cmd = new SQLiteCommand(insertSql, conn))
                            {
                                cmd.Parameters.Add("@token", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@etoken", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                                cmd.Parameters.Add("@name", System.Data.DbType.String);
                                cmd.Parameters.Add("@expiry", System.Data.DbType.String);
                                cmd.Parameters.Add("@strike", System.Data.DbType.Double);
                                cmd.Parameters.Add("@tick", System.Data.DbType.Double);
                                cmd.Parameters.Add("@lot", System.Data.DbType.Int32);
                                cmd.Parameters.Add("@type", System.Data.DbType.String);
                                cmd.Parameters.Add("@segment", System.Data.DbType.String);
                                cmd.Parameters.Add("@exchange", System.Data.DbType.String);
                                cmd.Parameters.Add("@underlying", System.Data.DbType.String);

                                var lines = csv.Split('\n');
                                int count = 0;
                                foreach (var line in lines.Skip(1)) // Skip header
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    var fields = line.Split(',');
                                    if (fields.Length < 12) continue;

                                    try
                                    {
                                        cmd.Parameters["@token"].Value = long.Parse(fields[0]);
                                        cmd.Parameters["@etoken"].Value = long.Parse(fields[1]);
                                        cmd.Parameters["@symbol"].Value = fields[2];
                                        cmd.Parameters["@name"].Value = fields[3];
                                        // Skip last_price (fields[4]) - not stored
                                        cmd.Parameters["@expiry"].Value = fields[5];
                                        cmd.Parameters["@strike"].Value = string.IsNullOrEmpty(fields[6]) ? 0 : double.Parse(fields[6]);
                                        cmd.Parameters["@tick"].Value = double.Parse(fields[7]);
                                        cmd.Parameters["@lot"].Value = int.Parse(fields[8]);
                                        cmd.Parameters["@type"].Value = fields[9];
                                        cmd.Parameters["@segment"].Value = fields[10];
                                        cmd.Parameters["@exchange"].Value = fields[11].TrimEnd('\r');

                                        // Derive underlying: for F&O, name column contains underlying
                                        // For equities, it's the tradingsymbol itself
                                        string underlying = fields[3]; // name column
                                        if (string.IsNullOrEmpty(underlying))
                                        {
                                            underlying = fields[2]; // use tradingsymbol as fallback
                                        }
                                        cmd.Parameters["@underlying"].Value = underlying;

                                        await cmd.ExecuteNonQueryAsync();
                                        count++;
                                    }
                                    catch (Exception ex)
                                    {
                                        // Skip malformed rows
                                        Logger.Debug($"[IDB] Skipping malformed row: {line.Substring(0, Math.Min(100, line.Length))}... Error: {ex.Message}");
                                    }
                                }
                                Logger.Info($"[IDB] Inserted {count} instruments into database.");
                            }
                            transaction.Commit();
                        }
                    }

                    // Reload cache after download
                    _cacheLoaded = false;
                    _symbolToTokenCache.Clear();
                    LoadAllTokensToCache();

                    Logger.Info("[IDB] Instrument database updated successfully.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[IDB] Failed to download/update instruments:", ex);
                return false;
            }
        }

        /// <summary>
        /// Multi-tier token lookup matching original InstrumentManager logic:
        /// 1. Check in-memory cache first
        /// 2. Try exact match on tradingsymbol
        /// 3. Try case-insensitive match on tradingsymbol
        /// 4. Try match on name for indices (NSE, BSE, INDICES segments)
        /// </summary>
        public long LookupToken(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return 0;

            // 1. Check in-memory cache first
            if (_symbolToTokenCache.TryGetValue(symbol, out long cachedToken))
                return cachedToken;

            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return 0;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();

                    // 2. Try exact match on tradingsymbol first
                    using (var cmd = new SQLiteCommand("SELECT instrument_token FROM instruments WHERE tradingsymbol = @symbol LIMIT 1", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            long token = Convert.ToInt64(result);
                            _symbolToTokenCache[symbol] = token; // Cache it
                            return token;
                        }
                    }

                    // 3. Try case-insensitive match on tradingsymbol
                    using (var cmd = new SQLiteCommand("SELECT instrument_token FROM instruments WHERE tradingsymbol = @symbol COLLATE NOCASE LIMIT 1", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            long token = Convert.ToInt64(result);
                            _symbolToTokenCache[symbol] = token; // Cache it
                            return token;
                        }
                    }

                    // 4. Try match on name for indices (NSE, BSE, INDICES segments)
                    using (var cmd = new SQLiteCommand("SELECT instrument_token FROM instruments WHERE name = @symbol COLLATE NOCASE AND segment IN ('NSE', 'BSE', 'INDICES') LIMIT 1", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            long token = Convert.ToInt64(result);
                            _symbolToTokenCache[symbol] = token; // Cache it
                            return token;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IDB] Token lookup failed for '{symbol}':", ex);
            }
            return 0;
        }

        /// <summary>
        /// Looks up option token using underlying column (NOT name column).
        /// This is critical for F&O instrument lookups.
        /// </summary>
        public long LookupOptionToken(string segment, string underlying, string expiry, double strike, string optionType)
        {
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return 0;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    // Use underlying column for F&O lookups, NOT name
                    string sql = @"SELECT instrument_token FROM instruments
                                   WHERE segment = @seg
                                   AND underlying = @und
                                   AND expiry = @exp
                                   AND strike = @str
                                   AND instrument_type = @typ
                                   LIMIT 1";
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@seg", segment);
                        cmd.Parameters.AddWithValue("@und", underlying);
                        cmd.Parameters.AddWithValue("@exp", expiry);
                        cmd.Parameters.AddWithValue("@str", strike);
                        cmd.Parameters.AddWithValue("@typ", optionType);
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
                Logger.Error($"[IDB] Option token lookup failed for {segment}/{underlying}/{expiry}/{strike}/{optionType}:", ex);
            }
            return 0;
        }

        /// <summary>
        /// Looks up option details (token and tradingsymbol) using underlying column.
        /// Matches original LookupOptionDetailsInSqlite behavior.
        /// </summary>
        public (long token, string symbol) LookupOptionDetails(string segment, string underlying, string expiry, double strike, string optionType)
        {
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return (0, null);

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    // Use underlying column for F&O lookups
                    string sql = @"SELECT instrument_token, tradingsymbol FROM instruments
                                   WHERE segment = @seg
                                   AND underlying = @und
                                   AND expiry = @exp
                                   AND strike = @str
                                   AND instrument_type = @typ
                                   LIMIT 1";
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@seg", segment);
                        cmd.Parameters.AddWithValue("@und", underlying);
                        cmd.Parameters.AddWithValue("@exp", expiry);
                        cmd.Parameters.AddWithValue("@str", strike);
                        cmd.Parameters.AddWithValue("@typ", optionType);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                long token = reader.GetInt64(0);
                                string tradingSymbol = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                Logger.Debug($"[IDB] Found option {tradingSymbol} (token={token}) for {underlying} {expiry} {strike} {optionType}");
                                return (token, tradingSymbol);
                            }
                        }
                    }
                }
                Logger.Warn($"[IDB] No option found for {segment} {underlying} {expiry} {strike} {optionType}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[IDB] Option details lookup failed:", ex);
            }
            return (0, null);
        }

        /// <summary>
        /// Gets unique expiry dates for an underlying using the underlying column.
        /// </summary>
        public List<DateTime> GetExpiries(string underlying)
        {
            var expiries = new List<DateTime>();
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return expiries;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    // Use underlying column, not name
                    using (var cmd = new SQLiteCommand("SELECT DISTINCT expiry FROM instruments WHERE underlying = @und AND expiry IS NOT NULL AND expiry != '' ORDER BY expiry", conn))
                    {
                        cmd.Parameters.AddWithValue("@und", underlying);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string dateStr = reader.GetString(0);
                                if (DateTime.TryParse(dateStr, out var dt))
                                    expiries.Add(dt);
                            }
                        }
                    }
                }
                Logger.Debug($"[IDB] Found {expiries.Count} expiries for {underlying}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[IDB] Error fetching expiries for {underlying}:", ex);
            }
            return expiries;
        }

        /// <summary>
        /// Gets lot size for an underlying using the underlying column.
        /// Looks for options (CE/PE) instruments to get the lot size.
        /// </summary>
        public int GetLotSize(string underlying)
        {
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return 0;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    // Use underlying column, look for option instruments to get lot size
                    using (var cmd = new SQLiteCommand(@"SELECT lot_size FROM instruments
                                                         WHERE underlying = @und
                                                         AND instrument_type IN ('CE', 'PE')
                                                         AND lot_size > 0
                                                         LIMIT 1", conn))
                    {
                        cmd.Parameters.AddWithValue("@und", underlying);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            int lotSize = Convert.ToInt32(result);
                            Logger.Debug($"[IDB] Found lot size {lotSize} for {underlying}");
                            return lotSize;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IDB] Error fetching lot size for {underlying}:", ex);
            }
            return 0;
        }

        public string GetSegmentForToken(long token)
        {
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return null;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("SELECT segment FROM instruments WHERE instrument_token = @token", conn))
                    {
                        cmd.Parameters.AddWithValue("@token", token);
                        var result = cmd.ExecuteScalar();
                        return result?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IDB] Error fetching segment for token {token}:", ex);
            }
            return null;
        }

        public async Task<List<MappedInstrument>> GetAllInstrumentsAsync()
        {
            var list = new List<MappedInstrument>();
            if (string.IsNullOrEmpty(_dbPath) || !File.Exists(_dbPath))
                return list;

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    await conn.OpenAsync();
                    using (var cmd = new SQLiteCommand("SELECT instrument_token, exchange_token, tradingsymbol, name, expiry, strike, tick_size, lot_size, instrument_type, segment, exchange, underlying FROM instruments", conn))
                    {
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                list.Add(new MappedInstrument
                                {
                                    instrument_token = reader.GetInt64(0),
                                    exchange_token = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
                                    symbol = reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    name = reader.IsDBNull(3) ? "" : reader.GetString(3),
                                    expiry = reader.IsDBNull(4) ? (DateTime?)null : (DateTime.TryParse(reader.GetString(4), out var dt) ? dt : (DateTime?)null),
                                    strike = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),
                                    tick_size = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                                    lot_size = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                                    instrument_type = reader.IsDBNull(8) ? "" : reader.GetString(8),
                                    segment = reader.IsDBNull(9) ? "" : reader.GetString(9),
                                    exchange = reader.IsDBNull(10) ? "" : reader.GetString(10),
                                    underlying = reader.IsDBNull(11) ? "" : reader.GetString(11),
                                    is_index = !reader.IsDBNull(8) && reader.GetString(8) == "EQ" &&
                                              !reader.IsDBNull(2) && (reader.GetString(2) == "NIFTY 50" ||
                                                                      reader.GetString(2) == "SENSEX" ||
                                                                      reader.GetString(2) == "NIFTY BANK" ||
                                                                      reader.GetString(2) == "NIFTY FIN SERVICE")
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[IDB] Error in GetAllInstrumentsAsync:", ex);
            }
            return list;
        }
    }
}
