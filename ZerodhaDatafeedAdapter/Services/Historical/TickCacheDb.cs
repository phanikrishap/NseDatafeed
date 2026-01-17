using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// SQLite-based cache for historical tick data.
    /// Shared by both ICICI and Accelpix tick data sources.
    /// Stores tick data locally to avoid redundant API calls.
    ///
    /// Optimized schema v2:
    /// - Uses symbol_id lookup to avoid repeating symbol strings
    /// - Uses epoch INTEGER for timestamps (4 bytes vs 19 bytes text)
    /// - Single price column (ticks don't need OHLC)
    /// - ~20 bytes per tick vs ~150 bytes in v1
    /// </summary>
    public class TickCacheDb : IDisposable
    {
        #region Singleton

        private static readonly Lazy<TickCacheDb> _instance = new Lazy<TickCacheDb>(() => new TickCacheDb());
        public static TickCacheDb Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly object _dbLock = new object();
        private bool _isInitialized = false;
        private const int SCHEMA_VERSION = 2;

        // Symbol ID cache to avoid repeated lookups
        private readonly Dictionary<string, int> _symbolIdCache = new Dictionary<string, int>();
        private readonly Dictionary<int, string> _idSymbolCache = new Dictionary<int, string>();

        private static readonly ILoggerService Logger = LoggerFactory.HistoricalTick;

        // Epoch for DateTime conversion (Unix epoch)
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Constructor

        private TickCacheDb()
        {
            // Store in ZerodhaAdapter folder alongside logs
            string adapterFolder = Classes.Constants.GetFolderPath("Cache");

            Directory.CreateDirectory(adapterFolder);
            _dbPath = Path.Combine(adapterFolder, "tick_cache_v2.db");
            _connectionString = $"Data Source={_dbPath};Version=3;Journal Mode=WAL;Synchronous=Normal;";

            Initialize();
        }

        #endregion

        #region Initialization

        private void Initialize()
        {
            try
            {
                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        // Optimized schema v2:
                        // - symbols table for symbol ID lookup (saves ~16 bytes per tick)
                        // - ticks table with INTEGER epoch time and single price
                        // - Estimated ~20 bytes per tick vs ~150 bytes in v1
                        string createTableSql = @"
                            CREATE TABLE IF NOT EXISTS symbols (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                symbol TEXT NOT NULL UNIQUE
                            );

                            CREATE TABLE IF NOT EXISTS ticks (
                                symbol_id INTEGER NOT NULL,
                                trade_date INTEGER NOT NULL,
                                tick_time INTEGER NOT NULL,
                                price REAL NOT NULL,
                                volume INTEGER NOT NULL,
                                oi INTEGER DEFAULT 0,
                                FOREIGN KEY (symbol_id) REFERENCES symbols(id)
                            );

                            CREATE INDEX IF NOT EXISTS idx_ticks_lookup
                            ON ticks(symbol_id, trade_date);

                            CREATE TABLE IF NOT EXISTS cache_metadata (
                                symbol_id INTEGER NOT NULL,
                                trade_date INTEGER NOT NULL,
                                tick_count INTEGER NOT NULL,
                                first_tick INTEGER NOT NULL,
                                last_tick INTEGER NOT NULL,
                                cached_at INTEGER NOT NULL,
                                PRIMARY KEY (symbol_id, trade_date),
                                FOREIGN KEY (symbol_id) REFERENCES symbols(id)
                            );

                            CREATE TABLE IF NOT EXISTS schema_info (
                                version INTEGER NOT NULL
                            );
                        ";

                        using (var cmd = new SQLiteCommand(createTableSql, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Check/set schema version
                        using (var cmd = new SQLiteCommand("SELECT version FROM schema_info LIMIT 1", connection))
                        {
                            var result = cmd.ExecuteScalar();
                            if (result == null)
                            {
                                using (var insertCmd = new SQLiteCommand($"INSERT INTO schema_info (version) VALUES ({SCHEMA_VERSION})", connection))
                                {
                                    insertCmd.ExecuteNonQuery();
                                }
                            }
                        }

                        // Load symbol cache
                        LoadSymbolCache(connection);
                    }

                    _isInitialized = true;
                    Logger.Info($"[TickCacheDb] Initialized v{SCHEMA_VERSION} at {_dbPath} ({_symbolIdCache.Count} symbols cached)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] Initialization error: {ex.Message}");
            }
        }

        private void LoadSymbolCache(SQLiteConnection connection)
        {
            _symbolIdCache.Clear();
            _idSymbolCache.Clear();

            using (var cmd = new SQLiteCommand("SELECT id, symbol FROM symbols", connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string symbol = reader.GetString(1);
                    _symbolIdCache[symbol] = id;
                    _idSymbolCache[id] = symbol;
                }
            }
        }

        private int GetOrCreateSymbolId(SQLiteConnection connection, string symbol)
        {
            if (_symbolIdCache.TryGetValue(symbol, out int cachedId))
                return cachedId;

            // Insert new symbol
            using (var cmd = new SQLiteCommand("INSERT OR IGNORE INTO symbols (symbol) VALUES (@symbol)", connection))
            {
                cmd.Parameters.AddWithValue("@symbol", symbol);
                cmd.ExecuteNonQuery();
            }

            // Get the ID
            using (var cmd = new SQLiteCommand("SELECT id FROM symbols WHERE symbol = @symbol", connection))
            {
                cmd.Parameters.AddWithValue("@symbol", symbol);
                int id = Convert.ToInt32(cmd.ExecuteScalar());
                _symbolIdCache[symbol] = id;
                _idSymbolCache[id] = symbol;
                return id;
            }
        }

        private static long ToEpoch(DateTime dt)
        {
            return (long)(dt.ToUniversalTime() - UnixEpoch).TotalSeconds;
        }

        private static DateTime FromEpoch(long epoch)
        {
            return UnixEpoch.AddSeconds(epoch).ToLocalTime();
        }

        private static int ToDateInt(DateTime dt)
        {
            // Store date as YYYYMMDD integer for efficient storage and comparison
            return dt.Year * 10000 + dt.Month * 100 + dt.Day;
        }

        #endregion

        #region Cache Operations

        /// <summary>
        /// Check if we have cached tick data for a symbol and date.
        /// </summary>
        public bool HasCachedData(string symbol, DateTime tradeDate)
        {
            if (!_isInitialized || string.IsNullOrEmpty(symbol))
                return false;

            try
            {
                // Quick check using in-memory symbol cache
                if (!_symbolIdCache.TryGetValue(symbol, out int symbolId))
                    return false;

                int dateInt = ToDateInt(tradeDate);

                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();
                        string sql = @"SELECT tick_count FROM cache_metadata
                                       WHERE symbol_id = @sid AND trade_date = @date";

                        using (var cmd = new SQLiteCommand(sql, connection))
                        {
                            cmd.Parameters.AddWithValue("@sid", symbolId);
                            cmd.Parameters.AddWithValue("@date", dateInt);

                            var result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                            {
                                int tickCount = Convert.ToInt32(result);
                                Logger.Debug($"[TickCacheDb] Cache HIT: {symbol} {tradeDate:yyyy-MM-dd} ({tickCount} ticks)");
                                return tickCount > 0;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] HasCachedData error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Get cached tick data for a symbol and date.
        /// Returns null if not cached.
        /// </summary>
        public List<HistoricalCandle> GetCachedTicks(string symbol, DateTime tradeDate)
        {
            if (!_isInitialized || string.IsNullOrEmpty(symbol))
                return null;

            try
            {
                if (!_symbolIdCache.TryGetValue(symbol, out int symbolId))
                    return null;

                int dateInt = ToDateInt(tradeDate);

                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();
                        string sql = @"SELECT tick_time, price, volume, oi
                                       FROM ticks
                                       WHERE symbol_id = @sid AND trade_date = @date
                                       ORDER BY tick_time ASC";

                        var candles = new List<HistoricalCandle>();

                        using (var cmd = new SQLiteCommand(sql, connection))
                        {
                            cmd.Parameters.AddWithValue("@sid", symbolId);
                            cmd.Parameters.AddWithValue("@date", dateInt);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    decimal price = (decimal)reader.GetDouble(1);
                                    candles.Add(new HistoricalCandle
                                    {
                                        DateTime = FromEpoch(reader.GetInt64(0)),
                                        Open = price,
                                        High = price,
                                        Low = price,
                                        Close = price,
                                        Volume = reader.GetInt64(2),
                                        OpenInterest = reader.GetInt64(3)
                                    });
                                }
                            }
                        }

                        if (candles.Count > 0)
                        {
                            Logger.Info($"[TickCacheDb] Retrieved {candles.Count} ticks from cache for {symbol} {tradeDate:yyyy-MM-dd}");
                            return candles;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] GetCachedTicks error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Store tick data in the cache. Filters out zero-volume ticks and deduplicates.
        /// </summary>
        public void CacheTicks(string symbol, DateTime tradeDate, List<HistoricalCandle> candles)
        {
            if (!_isInitialized || string.IsNullOrEmpty(symbol) || candles == null || candles.Count == 0)
                return;

            try
            {
                int originalCount = candles.Count;

                // Step 1: Filter out zero-volume ticks
                var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                int afterZeroFilter = filteredCandles.Count;
                int zeroVolumeRemoved = originalCount - afterZeroFilter;

                // Step 2: Deduplicate by time + price + volume (prune identical ticks)
                var deduplicatedCandles = filteredCandles
                    .GroupBy(c => new { c.DateTime, c.Close, c.Volume })
                    .Select(g => g.First())
                    .OrderBy(c => c.DateTime)
                    .ToList();

                int afterDedup = deduplicatedCandles.Count;
                int duplicatesRemoved = afterZeroFilter - afterDedup;

                if (zeroVolumeRemoved > 0 || duplicatesRemoved > 0)
                {
                    Logger.Info($"[TickCacheDb] Pruned {symbol}: {originalCount} -> {afterDedup} " +
                               $"(zero-vol: -{zeroVolumeRemoved}, dupes: -{duplicatesRemoved})");
                }

                if (deduplicatedCandles.Count == 0)
                {
                    Logger.Warn($"[TickCacheDb] No ticks to cache for {symbol} {tradeDate:yyyy-MM-dd} after filtering");
                    return;
                }

                int dateInt = ToDateInt(tradeDate);

                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        // Get or create symbol ID
                        int symbolId = GetOrCreateSymbolId(connection, symbol);

                        using (var transaction = connection.BeginTransaction())
                        {
                            try
                            {
                                // Delete existing data for this symbol/date
                                string deleteSql = "DELETE FROM ticks WHERE symbol_id = @sid AND trade_date = @date";
                                using (var deleteCmd = new SQLiteCommand(deleteSql, connection, transaction))
                                {
                                    deleteCmd.Parameters.AddWithValue("@sid", symbolId);
                                    deleteCmd.Parameters.AddWithValue("@date", dateInt);
                                    deleteCmd.ExecuteNonQuery();
                                }

                                // Insert new ticks using optimized schema
                                string insertSql = @"INSERT INTO ticks
                                    (symbol_id, trade_date, tick_time, price, volume, oi)
                                    VALUES (@sid, @date, @time, @price, @volume, @oi)";

                                using (var insertCmd = new SQLiteCommand(insertSql, connection, transaction))
                                {
                                    insertCmd.Parameters.Add("@sid", System.Data.DbType.Int32);
                                    insertCmd.Parameters.Add("@date", System.Data.DbType.Int32);
                                    insertCmd.Parameters.Add("@time", System.Data.DbType.Int64);
                                    insertCmd.Parameters.Add("@price", System.Data.DbType.Double);
                                    insertCmd.Parameters.Add("@volume", System.Data.DbType.Int64);
                                    insertCmd.Parameters.Add("@oi", System.Data.DbType.Int64);

                                    foreach (var candle in deduplicatedCandles)
                                    {
                                        insertCmd.Parameters["@sid"].Value = symbolId;
                                        insertCmd.Parameters["@date"].Value = dateInt;
                                        insertCmd.Parameters["@time"].Value = ToEpoch(candle.DateTime);
                                        insertCmd.Parameters["@price"].Value = (double)candle.Close;
                                        insertCmd.Parameters["@volume"].Value = candle.Volume;
                                        insertCmd.Parameters["@oi"].Value = candle.OpenInterest;

                                        insertCmd.ExecuteNonQuery();
                                    }
                                }

                                // Update metadata
                                string metaSql = @"INSERT OR REPLACE INTO cache_metadata
                                    (symbol_id, trade_date, tick_count, first_tick, last_tick, cached_at)
                                    VALUES (@sid, @date, @count, @first, @last, @now)";

                                using (var metaCmd = new SQLiteCommand(metaSql, connection, transaction))
                                {
                                    metaCmd.Parameters.AddWithValue("@sid", symbolId);
                                    metaCmd.Parameters.AddWithValue("@date", dateInt);
                                    metaCmd.Parameters.AddWithValue("@count", deduplicatedCandles.Count);
                                    metaCmd.Parameters.AddWithValue("@first", ToEpoch(deduplicatedCandles.First().DateTime));
                                    metaCmd.Parameters.AddWithValue("@last", ToEpoch(deduplicatedCandles.Last().DateTime));
                                    metaCmd.Parameters.AddWithValue("@now", ToEpoch(DateTime.Now));
                                    metaCmd.ExecuteNonQuery();
                                }

                                transaction.Commit();
                                Logger.Info($"[TickCacheDb] Cached {deduplicatedCandles.Count} ticks for {symbol} {tradeDate:yyyy-MM-dd}");
                            }
                            catch
                            {
                                transaction.Rollback();
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] CacheTicks error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public (int symbolCount, int totalTicks, long dbSizeKb) GetCacheStats()
        {
            if (!_isInitialized)
                return (0, 0, 0);

            try
            {
                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        int symbolCount = 0;
                        int totalTicks = 0;

                        using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM cache_metadata", connection))
                        {
                            symbolCount = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        using (var cmd = new SQLiteCommand("SELECT SUM(tick_count) FROM cache_metadata", connection))
                        {
                            var result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                                totalTicks = Convert.ToInt32(result);
                        }

                        long dbSizeKb = new FileInfo(_dbPath).Length / 1024;

                        return (symbolCount, totalTicks, dbSizeKb);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] GetCacheStats error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// Delete cached ticks for a specific symbol.
        /// Called after NT8 has successfully persisted the data.
        /// </summary>
        /// <param name="symbol">The symbol to delete cache for</param>
        /// <returns>Number of tick records deleted</returns>
        public int DeleteCacheForSymbol(string symbol)
        {
            if (!_isInitialized || string.IsNullOrEmpty(symbol))
                return 0;

            try
            {
                if (!_symbolIdCache.TryGetValue(symbol, out int symbolId))
                {
                    Logger.Debug($"[TickCacheDb] DeleteCacheForSymbol: Symbol not in cache: {symbol}");
                    return 0;
                }

                int deletedTicks = 0;

                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        using (var transaction = connection.BeginTransaction())
                        {
                            // Delete ticks for this symbol
                            string deleteTicksSql = "DELETE FROM ticks WHERE symbol_id = @sid";
                            using (var cmd = new SQLiteCommand(deleteTicksSql, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", symbolId);
                                deletedTicks = cmd.ExecuteNonQuery();
                            }

                            // Delete metadata for this symbol
                            string deleteMetaSql = "DELETE FROM cache_metadata WHERE symbol_id = @sid";
                            using (var cmd = new SQLiteCommand(deleteMetaSql, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", symbolId);
                                cmd.ExecuteNonQuery();
                            }

                            // Also delete the symbol from symbols table
                            string deleteSymbolSql = "DELETE FROM symbols WHERE id = @sid";
                            using (var cmd = new SQLiteCommand(deleteSymbolSql, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@sid", symbolId);
                                cmd.ExecuteNonQuery();
                            }

                            transaction.Commit();
                        }

                        // Remove from in-memory cache
                        _symbolIdCache.Remove(symbol);
                        _idSymbolCache.Remove(symbolId);

                        if (deletedTicks > 0)
                        {
                            Logger.Info($"[TickCacheDb] Deleted {deletedTicks} cached ticks for {symbol}");
                        }

                        // Check remaining ticks and vacuum if low (to reclaim disk space)
                        int remainingTicks = 0;
                        using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM ticks", connection))
                        {
                            remainingTicks = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        // Vacuum when database is empty or has very few remaining ticks
                        // This reclaims disk space from deleted records
                        if (remainingTicks < 1000)
                        {
                            try
                            {
                                using (var cmd = new SQLiteCommand("VACUUM", connection))
                                {
                                    cmd.ExecuteNonQuery();
                                }
                                Logger.Info($"[TickCacheDb] VACUUMed database (remaining ticks: {remainingTicks})");
                            }
                            catch (Exception vacuumEx)
                            {
                                Logger.Debug($"[TickCacheDb] VACUUM skipped: {vacuumEx.Message}");
                            }
                        }
                    }
                }

                return deletedTicks;
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] DeleteCacheForSymbol error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Clear old cache entries (older than specified days).
        /// </summary>
        public void ClearOldCache(int daysToKeep = 7)
        {
            if (!_isInitialized)
                return;

            try
            {
                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        int cutoffDateInt = ToDateInt(DateTime.Now.AddDays(-daysToKeep));

                        using (var transaction = connection.BeginTransaction())
                        {
                            string deleteTicks = "DELETE FROM ticks WHERE trade_date < @cutoff";
                            string deleteMeta = "DELETE FROM cache_metadata WHERE trade_date < @cutoff";

                            using (var cmd = new SQLiteCommand(deleteTicks, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@cutoff", cutoffDateInt);
                                int deleted = cmd.ExecuteNonQuery();
                                Logger.Info($"[TickCacheDb] Cleared {deleted} old tick records");
                            }

                            using (var cmd = new SQLiteCommand(deleteMeta, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@cutoff", cutoffDateInt);
                                cmd.ExecuteNonQuery();
                            }

                            transaction.Commit();
                        }

                        // Vacuum to reclaim space
                        using (var cmd = new SQLiteCommand("VACUUM", connection))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[TickCacheDb] ClearOldCache error: {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // SQLite connections are managed per-operation, no persistent connection to dispose
            Logger.Info("[TickCacheDb] Disposed");
        }

        #endregion
    }

    /// <summary>
    /// [Deprecated] Use TickCacheDb instead.
    /// This static class provides backward compatibility for code using IciciTickCacheDb.
    /// </summary>
    public static class IciciTickCacheDb
    {
        /// <summary>
        /// Gets the singleton instance (delegates to TickCacheDb.Instance).
        /// </summary>
        public static TickCacheDb Instance => TickCacheDb.Instance;
    }
}
