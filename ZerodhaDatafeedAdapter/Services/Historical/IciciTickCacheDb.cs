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
    /// SQLite-based cache for ICICI historical tick data.
    /// Stores tick data locally to avoid redundant API calls.
    /// </summary>
    public class IciciTickCacheDb : IDisposable
    {
        #region Singleton

        private static readonly Lazy<IciciTickCacheDb> _instance = new Lazy<IciciTickCacheDb>(() => new IciciTickCacheDb());
        public static IciciTickCacheDb Instance => _instance.Value;

        #endregion

        #region Fields

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly object _dbLock = new object();
        private bool _isInitialized = false;

        private static readonly ILoggerService Logger = LoggerFactory.IciciApi;

        #endregion

        #region Constructor

        private IciciTickCacheDb()
        {
            // Store in ZerodhaAdapter folder alongside logs
            string adapterFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "ZerodhaAdapter", "Cache");

            Directory.CreateDirectory(adapterFolder);
            _dbPath = Path.Combine(adapterFolder, "icici_tick_cache.db");
            _connectionString = $"Data Source={_dbPath};Version=3;";

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

                        // Create tick data table
                        string createTableSql = @"
                            CREATE TABLE IF NOT EXISTS tick_cache (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                symbol TEXT NOT NULL,
                                trade_date TEXT NOT NULL,
                                tick_time TEXT NOT NULL,
                                open REAL NOT NULL,
                                high REAL NOT NULL,
                                low REAL NOT NULL,
                                close REAL NOT NULL,
                                volume INTEGER NOT NULL,
                                open_interest INTEGER DEFAULT 0,
                                created_at TEXT DEFAULT CURRENT_TIMESTAMP
                            );

                            CREATE INDEX IF NOT EXISTS idx_tick_symbol_date
                            ON tick_cache(symbol, trade_date);

                            CREATE TABLE IF NOT EXISTS cache_metadata (
                                symbol TEXT NOT NULL,
                                trade_date TEXT NOT NULL,
                                tick_count INTEGER NOT NULL,
                                first_tick TEXT NOT NULL,
                                last_tick TEXT NOT NULL,
                                cached_at TEXT DEFAULT CURRENT_TIMESTAMP,
                                PRIMARY KEY (symbol, trade_date)
                            );
                        ";

                        using (var cmd = new SQLiteCommand(createTableSql, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }

                    _isInitialized = true;
                    Logger.Info($"[IciciTickCacheDb] Initialized at {_dbPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IciciTickCacheDb] Initialization error: {ex.Message}");
            }
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
                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();
                        string sql = @"SELECT tick_count FROM cache_metadata
                                       WHERE symbol = @symbol AND trade_date = @date";

                        using (var cmd = new SQLiteCommand(sql, connection))
                        {
                            cmd.Parameters.AddWithValue("@symbol", symbol);
                            cmd.Parameters.AddWithValue("@date", tradeDate.ToString("yyyy-MM-dd"));

                            var result = cmd.ExecuteScalar();
                            if (result != null && result != DBNull.Value)
                            {
                                int tickCount = Convert.ToInt32(result);
                                Logger.Debug($"[IciciTickCacheDb] Cache HIT: {symbol} {tradeDate:yyyy-MM-dd} ({tickCount} ticks)");
                                return tickCount > 0;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IciciTickCacheDb] HasCachedData error: {ex.Message}");
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
                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();
                        string sql = @"SELECT tick_time, open, high, low, close, volume, open_interest
                                       FROM tick_cache
                                       WHERE symbol = @symbol AND trade_date = @date
                                       ORDER BY tick_time ASC";

                        var candles = new List<HistoricalCandle>();

                        using (var cmd = new SQLiteCommand(sql, connection))
                        {
                            cmd.Parameters.AddWithValue("@symbol", symbol);
                            cmd.Parameters.AddWithValue("@date", tradeDate.ToString("yyyy-MM-dd"));

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    candles.Add(new HistoricalCandle
                                    {
                                        DateTime = DateTime.Parse(reader.GetString(0)),
                                        Open = (decimal)reader.GetDouble(1),
                                        High = (decimal)reader.GetDouble(2),
                                        Low = (decimal)reader.GetDouble(3),
                                        Close = (decimal)reader.GetDouble(4),
                                        Volume = reader.GetInt64(5),
                                        OpenInterest = reader.GetInt64(6)
                                    });
                                }
                            }
                        }

                        if (candles.Count > 0)
                        {
                            Logger.Info($"[IciciTickCacheDb] Retrieved {candles.Count} ticks from cache for {symbol} {tradeDate:yyyy-MM-dd}");
                            return candles;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[IciciTickCacheDb] GetCachedTicks error: {ex.Message}");
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

                // Step 2: Deduplicate by time + OHLC + volume (prune identical ticks)
                var deduplicatedCandles = filteredCandles
                    .GroupBy(c => new { c.DateTime, c.Open, c.High, c.Low, c.Close, c.Volume })
                    .Select(g => g.First())
                    .OrderBy(c => c.DateTime)
                    .ToList();

                int afterDedup = deduplicatedCandles.Count;
                int duplicatesRemoved = afterZeroFilter - afterDedup;

                if (zeroVolumeRemoved > 0 || duplicatesRemoved > 0)
                {
                    Logger.Info($"[IciciTickCacheDb] Pruned {symbol}: {originalCount} -> {afterDedup} " +
                               $"(zero-vol: -{zeroVolumeRemoved}, dupes: -{duplicatesRemoved})");
                }

                if (deduplicatedCandles.Count == 0)
                {
                    Logger.Warn($"[IciciTickCacheDb] No ticks to cache for {symbol} {tradeDate:yyyy-MM-dd} after filtering");
                    return;
                }

                lock (_dbLock)
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();

                        using (var transaction = connection.BeginTransaction())
                        {
                            try
                            {
                                // Delete existing data for this symbol/date
                                string deleteSql = "DELETE FROM tick_cache WHERE symbol = @symbol AND trade_date = @date";
                                using (var deleteCmd = new SQLiteCommand(deleteSql, connection, transaction))
                                {
                                    deleteCmd.Parameters.AddWithValue("@symbol", symbol);
                                    deleteCmd.Parameters.AddWithValue("@date", tradeDate.ToString("yyyy-MM-dd"));
                                    deleteCmd.ExecuteNonQuery();
                                }

                                // Insert new ticks in batches
                                string insertSql = @"INSERT INTO tick_cache
                                    (symbol, trade_date, tick_time, open, high, low, close, volume, open_interest)
                                    VALUES (@symbol, @date, @time, @open, @high, @low, @close, @volume, @oi)";

                                using (var insertCmd = new SQLiteCommand(insertSql, connection, transaction))
                                {
                                    insertCmd.Parameters.Add("@symbol", System.Data.DbType.String);
                                    insertCmd.Parameters.Add("@date", System.Data.DbType.String);
                                    insertCmd.Parameters.Add("@time", System.Data.DbType.String);
                                    insertCmd.Parameters.Add("@open", System.Data.DbType.Double);
                                    insertCmd.Parameters.Add("@high", System.Data.DbType.Double);
                                    insertCmd.Parameters.Add("@low", System.Data.DbType.Double);
                                    insertCmd.Parameters.Add("@close", System.Data.DbType.Double);
                                    insertCmd.Parameters.Add("@volume", System.Data.DbType.Int64);
                                    insertCmd.Parameters.Add("@oi", System.Data.DbType.Int64);

                                    foreach (var candle in deduplicatedCandles)
                                    {
                                        insertCmd.Parameters["@symbol"].Value = symbol;
                                        insertCmd.Parameters["@date"].Value = tradeDate.ToString("yyyy-MM-dd");
                                        insertCmd.Parameters["@time"].Value = candle.DateTime.ToString("yyyy-MM-dd HH:mm:ss");
                                        insertCmd.Parameters["@open"].Value = (double)candle.Open;
                                        insertCmd.Parameters["@high"].Value = (double)candle.High;
                                        insertCmd.Parameters["@low"].Value = (double)candle.Low;
                                        insertCmd.Parameters["@close"].Value = (double)candle.Close;
                                        insertCmd.Parameters["@volume"].Value = candle.Volume;
                                        insertCmd.Parameters["@oi"].Value = candle.OpenInterest;

                                        insertCmd.ExecuteNonQuery();
                                    }
                                }

                                // Update metadata
                                string metaSql = @"INSERT OR REPLACE INTO cache_metadata
                                    (symbol, trade_date, tick_count, first_tick, last_tick, cached_at)
                                    VALUES (@symbol, @date, @count, @first, @last, @now)";

                                using (var metaCmd = new SQLiteCommand(metaSql, connection, transaction))
                                {
                                    metaCmd.Parameters.AddWithValue("@symbol", symbol);
                                    metaCmd.Parameters.AddWithValue("@date", tradeDate.ToString("yyyy-MM-dd"));
                                    metaCmd.Parameters.AddWithValue("@count", deduplicatedCandles.Count);
                                    metaCmd.Parameters.AddWithValue("@first", deduplicatedCandles.First().DateTime.ToString("HH:mm:ss"));
                                    metaCmd.Parameters.AddWithValue("@last", deduplicatedCandles.Last().DateTime.ToString("HH:mm:ss"));
                                    metaCmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                                    metaCmd.ExecuteNonQuery();
                                }

                                transaction.Commit();
                                Logger.Info($"[IciciTickCacheDb] Cached {deduplicatedCandles.Count} ticks for {symbol} {tradeDate:yyyy-MM-dd}");
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
                Logger.Error($"[IciciTickCacheDb] CacheTicks error: {ex.Message}");
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

                        using (var cmd = new SQLiteCommand("SELECT COUNT(DISTINCT symbol || trade_date) FROM cache_metadata", connection))
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
                Logger.Error($"[IciciTickCacheDb] GetCacheStats error: {ex.Message}");
                return (0, 0, 0);
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

                        DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep);
                        string cutoffStr = cutoffDate.ToString("yyyy-MM-dd");

                        using (var transaction = connection.BeginTransaction())
                        {
                            string deleteTicks = "DELETE FROM tick_cache WHERE trade_date < @cutoff";
                            string deleteMeta = "DELETE FROM cache_metadata WHERE trade_date < @cutoff";

                            using (var cmd = new SQLiteCommand(deleteTicks, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@cutoff", cutoffStr);
                                int deleted = cmd.ExecuteNonQuery();
                                Logger.Info($"[IciciTickCacheDb] Cleared {deleted} old tick records");
                            }

                            using (var cmd = new SQLiteCommand(deleteMeta, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@cutoff", cutoffStr);
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
                Logger.Error($"[IciciTickCacheDb] ClearOldCache error: {ex.Message}");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // SQLite connections are managed per-operation, no persistent connection to dispose
            Logger.Info("[IciciTickCacheDb] Disposed");
        }

        #endregion
    }
}
