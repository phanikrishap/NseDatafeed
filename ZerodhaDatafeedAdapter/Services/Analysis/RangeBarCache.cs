using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// SQLite cache for RangeATR bars and ticks.
    /// Provides fast loading of historical data instead of waiting for NinjaTrader BarsRequest.
    ///
    /// Database: Documents\NinjaTrader 8\ZerodhaAdapter\RangeBarCache.db
    ///
    /// Tables:
    ///   - range_bars: RangeATR bar OHLCV data
    ///   - ticks: Tick data with price/volume
    ///   - cache_meta: Metadata (last update, symbol info)
    /// </summary>
    public class RangeBarCache : IDisposable
    {
        private static readonly Lazy<RangeBarCache> _instance =
            new Lazy<RangeBarCache>(() => new RangeBarCache());
        public static RangeBarCache Instance => _instance.Value;

        private readonly string _dbPath;
        private readonly object _lock = new object();
        private bool _initialized = false;

        // Cache validity in hours (re-fetch from NT if older)
        private const int CACHE_VALIDITY_HOURS = 4;

        private RangeBarCache()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string adapterFolder = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter");

            if (!Directory.Exists(adapterFolder))
                Directory.CreateDirectory(adapterFolder);

            _dbPath = Path.Combine(adapterFolder, "RangeBarCache.db");
        }

        /// <summary>
        /// Initializes the cache database (creates tables if needed).
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;"))
                    {
                        conn.Open();

                        // Create tables if they don't exist
                        string createSql = @"
                            CREATE TABLE IF NOT EXISTS range_bars (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                symbol TEXT NOT NULL,
                                bar_time INTEGER NOT NULL,
                                open REAL NOT NULL,
                                high REAL NOT NULL,
                                low REAL NOT NULL,
                                close REAL NOT NULL,
                                volume INTEGER NOT NULL,
                                bar_index INTEGER NOT NULL
                            );

                            CREATE TABLE IF NOT EXISTS ticks (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                symbol TEXT NOT NULL,
                                tick_time INTEGER NOT NULL,
                                price REAL NOT NULL,
                                volume INTEGER NOT NULL,
                                is_buy INTEGER NOT NULL
                            );

                            CREATE TABLE IF NOT EXISTS cache_meta (
                                symbol TEXT PRIMARY KEY,
                                last_update INTEGER NOT NULL,
                                bars_count INTEGER NOT NULL,
                                ticks_count INTEGER NOT NULL,
                                oldest_bar INTEGER NOT NULL,
                                newest_bar INTEGER NOT NULL
                            );

                            CREATE INDEX IF NOT EXISTS idx_bars_symbol_time ON range_bars(symbol, bar_time);
                            CREATE INDEX IF NOT EXISTS idx_ticks_symbol_time ON ticks(symbol, tick_time);
                        ";

                        using (var cmd = new SQLiteCommand(createSql, conn))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }

                    _initialized = true;
                    Logger.Info($"[RangeBarCache] Initialized at {_dbPath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[RangeBarCache] Initialize failed: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Checks if the cache has valid data for the given symbol.
        /// Returns true if cache exists and was updated within CACHE_VALIDITY_HOURS.
        /// </summary>
        public bool HasValidCache(string symbol, DateTime cutoffDate)
        {
            Initialize();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();

                    string sql = "SELECT last_update, oldest_bar FROM cache_meta WHERE symbol = @symbol";
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                long lastUpdateTicks = reader.GetInt64(0);
                                long oldestBarTicks = reader.GetInt64(1);

                                var lastUpdate = new DateTime(lastUpdateTicks);
                                var oldestBar = new DateTime(oldestBarTicks);

                                // Check if cache is recent enough and covers the required date range
                                bool isRecent = (DateTime.Now - lastUpdate).TotalHours < CACHE_VALIDITY_HOURS;
                                bool coversRange = oldestBar <= cutoffDate;

                                return isRecent && coversRange;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[RangeBarCache] HasValidCache failed: {ex.Message}", ex);
            }

            return false;
        }

        /// <summary>
        /// Loads cached RangeATR bars for the given symbol.
        /// </summary>
        public List<CachedBar> LoadBars(string symbol, DateTime cutoffDate)
        {
            var bars = new List<CachedBar>();
            Initialize();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();

                    long cutoffTicks = cutoffDate.Ticks;
                    string sql = @"SELECT bar_time, open, high, low, close, volume, bar_index
                                   FROM range_bars
                                   WHERE symbol = @symbol AND bar_time >= @cutoff
                                   ORDER BY bar_time ASC";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.Parameters.AddWithValue("@cutoff", cutoffTicks);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                bars.Add(new CachedBar
                                {
                                    Time = new DateTime(reader.GetInt64(0)),
                                    Open = reader.GetDouble(1),
                                    High = reader.GetDouble(2),
                                    Low = reader.GetDouble(3),
                                    Close = reader.GetDouble(4),
                                    Volume = reader.GetInt64(5),
                                    Index = reader.GetInt32(6)
                                });
                            }
                        }
                    }
                }

                Logger.Info($"[RangeBarCache] Loaded {bars.Count} bars for {symbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RangeBarCache] LoadBars failed: {ex.Message}", ex);
            }

            return bars;
        }

        /// <summary>
        /// Loads cached ticks for the given symbol.
        /// </summary>
        public List<CachedTick> LoadTicks(string symbol, DateTime cutoffDate)
        {
            var ticks = new List<CachedTick>();
            Initialize();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Read Only=True;"))
                {
                    conn.Open();

                    long cutoffTicks = cutoffDate.Ticks;
                    string sql = @"SELECT tick_time, price, volume, is_buy
                                   FROM ticks
                                   WHERE symbol = @symbol AND tick_time >= @cutoff
                                   ORDER BY tick_time ASC";

                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.Parameters.AddWithValue("@cutoff", cutoffTicks);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ticks.Add(new CachedTick
                                {
                                    Time = new DateTime(reader.GetInt64(0)),
                                    Price = reader.GetDouble(1),
                                    Volume = reader.GetInt64(2),
                                    IsBuy = reader.GetInt32(3) == 1
                                });
                            }
                        }
                    }
                }

                Logger.Info($"[RangeBarCache] Loaded {ticks.Count} ticks for {symbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RangeBarCache] LoadTicks failed: {ex.Message}", ex);
            }

            return ticks;
        }

        /// <summary>
        /// Saves bars and ticks to the cache (replaces existing data for symbol).
        /// Uses a single transaction for speed.
        /// </summary>
        public void SaveToCache(string symbol, List<CachedBar> bars, List<CachedTick> ticks)
        {
            Initialize();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;"))
                {
                    conn.Open();

                    using (var transaction = conn.BeginTransaction())
                    {
                        // Delete existing data for this symbol
                        using (var cmd = new SQLiteCommand("DELETE FROM range_bars WHERE symbol = @symbol", conn))
                        {
                            cmd.Parameters.AddWithValue("@symbol", symbol);
                            cmd.ExecuteNonQuery();
                        }

                        using (var cmd = new SQLiteCommand("DELETE FROM ticks WHERE symbol = @symbol", conn))
                        {
                            cmd.Parameters.AddWithValue("@symbol", symbol);
                            cmd.ExecuteNonQuery();
                        }

                        // Insert bars using prepared statement
                        if (bars.Count > 0)
                        {
                            using (var cmd = new SQLiteCommand(
                                @"INSERT INTO range_bars (symbol, bar_time, open, high, low, close, volume, bar_index)
                                  VALUES (@symbol, @bar_time, @open, @high, @low, @close, @volume, @bar_index)", conn))
                            {
                                cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                                cmd.Parameters.Add("@bar_time", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@open", System.Data.DbType.Double);
                                cmd.Parameters.Add("@high", System.Data.DbType.Double);
                                cmd.Parameters.Add("@low", System.Data.DbType.Double);
                                cmd.Parameters.Add("@close", System.Data.DbType.Double);
                                cmd.Parameters.Add("@volume", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@bar_index", System.Data.DbType.Int32);

                                foreach (var bar in bars)
                                {
                                    cmd.Parameters["@symbol"].Value = symbol;
                                    cmd.Parameters["@bar_time"].Value = bar.Time.Ticks;
                                    cmd.Parameters["@open"].Value = bar.Open;
                                    cmd.Parameters["@high"].Value = bar.High;
                                    cmd.Parameters["@low"].Value = bar.Low;
                                    cmd.Parameters["@close"].Value = bar.Close;
                                    cmd.Parameters["@volume"].Value = bar.Volume;
                                    cmd.Parameters["@bar_index"].Value = bar.Index;
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }

                        // Insert ticks using prepared statement
                        if (ticks.Count > 0)
                        {
                            using (var cmd = new SQLiteCommand(
                                @"INSERT INTO ticks (symbol, tick_time, price, volume, is_buy)
                                  VALUES (@symbol, @tick_time, @price, @volume, @is_buy)", conn))
                            {
                                cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                                cmd.Parameters.Add("@tick_time", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@price", System.Data.DbType.Double);
                                cmd.Parameters.Add("@volume", System.Data.DbType.Int64);
                                cmd.Parameters.Add("@is_buy", System.Data.DbType.Int32);

                                foreach (var tick in ticks)
                                {
                                    cmd.Parameters["@symbol"].Value = symbol;
                                    cmd.Parameters["@tick_time"].Value = tick.Time.Ticks;
                                    cmd.Parameters["@price"].Value = tick.Price;
                                    cmd.Parameters["@volume"].Value = tick.Volume;
                                    cmd.Parameters["@is_buy"].Value = tick.IsBuy ? 1 : 0;
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }

                        // Update metadata
                        DateTime oldestBar = bars.Count > 0 ? bars.Min(b => b.Time) : DateTime.Now;
                        DateTime newestBar = bars.Count > 0 ? bars.Max(b => b.Time) : DateTime.Now;

                        using (var cmd = new SQLiteCommand(
                            @"INSERT OR REPLACE INTO cache_meta (symbol, last_update, bars_count, ticks_count, oldest_bar, newest_bar)
                              VALUES (@symbol, @last_update, @bars_count, @ticks_count, @oldest_bar, @newest_bar)", conn))
                        {
                            cmd.Parameters.AddWithValue("@symbol", symbol);
                            cmd.Parameters.AddWithValue("@last_update", DateTime.Now.Ticks);
                            cmd.Parameters.AddWithValue("@bars_count", bars.Count);
                            cmd.Parameters.AddWithValue("@ticks_count", ticks.Count);
                            cmd.Parameters.AddWithValue("@oldest_bar", oldestBar.Ticks);
                            cmd.Parameters.AddWithValue("@newest_bar", newestBar.Ticks);
                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }

                Logger.Info($"[RangeBarCache] Saved {bars.Count} bars and {ticks.Count} ticks for {symbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RangeBarCache] SaveToCache failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clears all cached data for the given symbol.
        /// </summary>
        public void ClearCache(string symbol)
        {
            Initialize();

            try
            {
                using (var conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;"))
                {
                    conn.Open();

                    using (var cmd = new SQLiteCommand("DELETE FROM range_bars WHERE symbol = @symbol", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SQLiteCommand("DELETE FROM ticks WHERE symbol = @symbol", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SQLiteCommand("DELETE FROM cache_meta WHERE symbol = @symbol", conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.ExecuteNonQuery();
                    }
                }

                Logger.Info($"[RangeBarCache] Cleared cache for {symbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RangeBarCache] ClearCache failed: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            // SQLite connections are per-operation, no persistent connection to dispose
        }
    }

    /// <summary>
    /// Cached bar data structure.
    /// </summary>
    public class CachedBar
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
        public int Index { get; set; }
    }

    /// <summary>
    /// Cached tick data structure.
    /// </summary>
    public class CachedTick
    {
        public DateTime Time { get; set; }
        public double Price { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
    }
}
