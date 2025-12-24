using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using QANinjaAdapter.Classes;

namespace QANinjaAdapter.Services.MarketData
{
    /// <summary>
    /// SQLite-based cache for historical OHLC bars.
    /// Stores bars fetched from Zerodha API to avoid redundant API calls.
    /// Used for CE/PE options and other instruments.
    /// </summary>
    public class HistoricalBarCache
    {
        private static HistoricalBarCache _instance;
        private static readonly object _lock = new object();

        private readonly string _dbPath;
        private readonly string _connectionString;

        public static HistoricalBarCache Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new HistoricalBarCache();
                    }
                }
                return _instance;
            }
        }

        private HistoricalBarCache()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dataFolder = Path.Combine(documentsPath, Constants.BaseDataFolder);

            if (!Directory.Exists(dataFolder))
                Directory.CreateDirectory(dataFolder);

            _dbPath = Path.Combine(dataFolder, "historical_bars.db");
            _connectionString = $"Data Source={_dbPath};Version=3;";

            InitializeDatabase();
            Logger.Info($"[HistoricalBarCache] Initialized with database at: {_dbPath}");
        }

        private void InitializeDatabase()
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    // Create historical_bars table
                    string createTable = @"
                        CREATE TABLE IF NOT EXISTS historical_bars (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            symbol TEXT NOT NULL,
                            interval TEXT NOT NULL,
                            timestamp TEXT NOT NULL,
                            open REAL NOT NULL,
                            high REAL NOT NULL,
                            low REAL NOT NULL,
                            close REAL NOT NULL,
                            volume REAL NOT NULL,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                            UNIQUE(symbol, interval, timestamp)
                        );
                        CREATE INDEX IF NOT EXISTS idx_hist_symbol ON historical_bars(symbol);
                        CREATE INDEX IF NOT EXISTS idx_hist_symbol_interval ON historical_bars(symbol, interval);
                        CREATE INDEX IF NOT EXISTS idx_hist_timestamp ON historical_bars(symbol, interval, timestamp);
                    ";

                    using (var cmd = new SQLiteCommand(createTable, conn))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    Logger.Info("[HistoricalBarCache] Database initialized successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to initialize database: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stores bars for a symbol in the cache
        /// </summary>
        public void StoreBars(string symbol, string interval, List<Record> bars)
        {
            if (string.IsNullOrEmpty(symbol) || bars == null || bars.Count == 0)
                return;

            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    using (var transaction = conn.BeginTransaction())
                    {
                        string insertSql = @"
                            INSERT OR REPLACE INTO historical_bars
                            (symbol, interval, timestamp, open, high, low, close, volume)
                            VALUES (@symbol, @interval, @timestamp, @open, @high, @low, @close, @volume)";

                        using (var cmd = new SQLiteCommand(insertSql, conn))
                        {
                            cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                            cmd.Parameters.Add("@interval", System.Data.DbType.String);
                            cmd.Parameters.Add("@timestamp", System.Data.DbType.String);
                            cmd.Parameters.Add("@open", System.Data.DbType.Double);
                            cmd.Parameters.Add("@high", System.Data.DbType.Double);
                            cmd.Parameters.Add("@low", System.Data.DbType.Double);
                            cmd.Parameters.Add("@close", System.Data.DbType.Double);
                            cmd.Parameters.Add("@volume", System.Data.DbType.Double);

                            foreach (var bar in bars)
                            {
                                cmd.Parameters["@symbol"].Value = symbol;
                                cmd.Parameters["@interval"].Value = interval;
                                cmd.Parameters["@timestamp"].Value = bar.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss");
                                cmd.Parameters["@open"].Value = bar.Open;
                                cmd.Parameters["@high"].Value = bar.High;
                                cmd.Parameters["@low"].Value = bar.Low;
                                cmd.Parameters["@close"].Value = bar.Close;
                                cmd.Parameters["@volume"].Value = bar.Volume;

                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                }

                Logger.Debug($"[HistoricalBarCache] Stored {bars.Count} bars for {symbol} ({interval})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to store bars: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Retrieves cached bars for a symbol within a date range
        /// </summary>
        public List<Record> GetCachedBars(string symbol, string interval, DateTime fromDate, DateTime toDate)
        {
            var records = new List<Record>();

            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    string selectSql = @"
                        SELECT timestamp, open, high, low, close, volume
                        FROM historical_bars
                        WHERE symbol = @symbol
                          AND interval = @interval
                          AND timestamp >= @fromDate
                          AND timestamp <= @toDate
                        ORDER BY timestamp";

                    using (var cmd = new SQLiteCommand(selectSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.Parameters.AddWithValue("@interval", interval);
                        cmd.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd HH:mm:ss"));

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                records.Add(new Record
                                {
                                    TimeStamp = DateTime.Parse(reader.GetString(0)),
                                    Open = reader.GetDouble(1),
                                    High = reader.GetDouble(2),
                                    Low = reader.GetDouble(3),
                                    Close = reader.GetDouble(4),
                                    Volume = reader.GetDouble(5)
                                });
                            }
                        }
                    }
                }

                if (records.Count > 0)
                {
                    Logger.Info($"[HistoricalBarCache] Retrieved {records.Count} cached bars for {symbol} ({interval})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to retrieve cached bars: {ex.Message}", ex);
            }

            return records;
        }

        /// <summary>
        /// Checks if cached data exists for a symbol within a date range
        /// </summary>
        public bool HasCachedData(string symbol, string interval, DateTime fromDate, DateTime toDate)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    string countSql = @"
                        SELECT COUNT(*) FROM historical_bars
                        WHERE symbol = @symbol
                          AND interval = @interval
                          AND timestamp >= @fromDate
                          AND timestamp <= @toDate";

                    using (var cmd = new SQLiteCommand(countSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", symbol);
                        cmd.Parameters.AddWithValue("@interval", interval);
                        cmd.Parameters.AddWithValue("@fromDate", fromDate.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@toDate", toDate.ToString("yyyy-MM-dd HH:mm:ss"));
                        var count = Convert.ToInt32(cmd.ExecuteScalar());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to check cached data: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Clears all cached data
        /// </summary>
        public void ClearCache()
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("DELETE FROM historical_bars", conn))
                    {
                        int deleted = cmd.ExecuteNonQuery();
                        Logger.Info($"[HistoricalBarCache] Cleared {deleted} cached bars");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to clear cache: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clears cached data older than a specified number of days
        /// </summary>
        public void ClearOldData(int daysToKeep = 7)
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-daysToKeep);

                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "DELETE FROM historical_bars WHERE timestamp < @cutoff", conn))
                    {
                        cmd.Parameters.AddWithValue("@cutoff", cutoffDate.ToString("yyyy-MM-dd HH:mm:ss"));
                        int deleted = cmd.ExecuteNonQuery();
                        Logger.Info($"[HistoricalBarCache] Cleared {deleted} bars older than {daysToKeep} days");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[HistoricalBarCache] Failed to clear old data: {ex.Message}", ex);
            }
        }
    }
}
