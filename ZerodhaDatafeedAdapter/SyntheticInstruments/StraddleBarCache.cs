using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using ZerodhaDatafeedAdapter.Classes;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// SQLite-based cache for synthetic straddle OHLC bars.
    /// Stores computed STRDL bars when CE/PE historical data is fetched.
    /// Provides cached data when user opens a STRDL chart.
    /// </summary>
    public class StraddleBarCache
    {
        private static StraddleBarCache _instance;
        private static readonly object _lock = new object();

        private readonly string _dbPath;
        private readonly string _connectionString;

        // In-memory pending bars waiting for matching leg data
        // Key: straddleSymbol, Value: dictionary of timestamp -> (ceBar, peBar)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<DateTime, (BarData ce, BarData pe)>> _pendingBars
            = new ConcurrentDictionary<string, ConcurrentDictionary<DateTime, (BarData, BarData)>>();

        public static StraddleBarCache Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new StraddleBarCache();
                    }
                }
                return _instance;
            }
        }

        private StraddleBarCache()
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string dataFolder = Path.Combine(documentsPath, Constants.BaseDataFolder);

            if (!Directory.Exists(dataFolder))
                Directory.CreateDirectory(dataFolder);

            _dbPath = Path.Combine(dataFolder, "straddle_bars.db");
            _connectionString = $"Data Source={_dbPath};Version=3;";

            InitializeDatabase();
            Logger.Info($"[StraddleBarCache] Initialized with database at: {_dbPath}");
        }

        private void InitializeDatabase()
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    // Create straddle_bars table
                    string createTable = @"
                        CREATE TABLE IF NOT EXISTS straddle_bars (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            straddle_symbol TEXT NOT NULL,
                            timestamp TEXT NOT NULL,
                            open REAL NOT NULL,
                            high REAL NOT NULL,
                            low REAL NOT NULL,
                            close REAL NOT NULL,
                            volume REAL NOT NULL,
                            ce_close REAL,
                            pe_close REAL,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                            UNIQUE(straddle_symbol, timestamp)
                        );
                        CREATE INDEX IF NOT EXISTS idx_straddle_symbol ON straddle_bars(straddle_symbol);
                        CREATE INDEX IF NOT EXISTS idx_straddle_timestamp ON straddle_bars(straddle_symbol, timestamp);
                    ";

                    using (var cmd = new SQLiteCommand(createTable, conn))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    Logger.Info("[StraddleBarCache] Database initialized successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to initialize database: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stores CE leg bars for a straddle. If PE bars already exist for same timestamps, combines and saves.
        /// </summary>
        public void StoreCEBars(string straddleSymbol, string ceSymbol, List<Record> bars)
        {
            if (string.IsNullOrEmpty(straddleSymbol) || bars == null || bars.Count == 0)
                return;

            Logger.Debug($"[StraddleBarCache] StoreCEBars: {straddleSymbol} with {bars.Count} bars");

            var pendingDict = _pendingBars.GetOrAdd(straddleSymbol, _ => new ConcurrentDictionary<DateTime, (BarData, BarData)>());

            foreach (var bar in bars)
            {
                var normalizedTime = NormalizeToMinute(bar.TimeStamp);
                var ceBar = new BarData
                {
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume
                };

                pendingDict.AddOrUpdate(normalizedTime,
                    (ceBar, null),
                    (key, existing) => (ceBar, existing.pe));
            }

            // Try to combine and save complete bars
            TryCombineAndSave(straddleSymbol);
        }

        /// <summary>
        /// Stores PE leg bars for a straddle. If CE bars already exist for same timestamps, combines and saves.
        /// </summary>
        public void StorePEBars(string straddleSymbol, string peSymbol, List<Record> bars)
        {
            if (string.IsNullOrEmpty(straddleSymbol) || bars == null || bars.Count == 0)
                return;

            Logger.Debug($"[StraddleBarCache] StorePEBars: {straddleSymbol} with {bars.Count} bars");

            var pendingDict = _pendingBars.GetOrAdd(straddleSymbol, _ => new ConcurrentDictionary<DateTime, (BarData, BarData)>());

            foreach (var bar in bars)
            {
                var normalizedTime = NormalizeToMinute(bar.TimeStamp);
                var peBar = new BarData
                {
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume
                };

                pendingDict.AddOrUpdate(normalizedTime,
                    (null, peBar),
                    (key, existing) => (existing.ce, peBar));
            }

            // Try to combine and save complete bars
            TryCombineAndSave(straddleSymbol);
        }

        /// <summary>
        /// Combines pending CE and PE bars and saves complete straddle bars to SQLite.
        /// Uses forward-fill for illiquid strikes where one leg may have gaps.
        /// </summary>
        private void TryCombineAndSave(string straddleSymbol)
        {
            if (!_pendingBars.TryGetValue(straddleSymbol, out var pendingDict))
                return;

            // Get all timestamps sorted
            var allTimestamps = pendingDict.Keys.OrderBy(t => t).ToList();
            if (allTimestamps.Count == 0)
                return;

            var completeBars = new List<(DateTime timestamp, BarData combined, double ceClose, double peClose)>();
            var completedTimestamps = new List<DateTime>();

            // Track last known values for forward-fill
            BarData lastKnownCE = null;
            BarData lastKnownPE = null;
            DateTime? firstCompleteTimestamp = null;

            // First pass: find first timestamp where BOTH legs have data
            foreach (var ts in allTimestamps)
            {
                var (ce, pe) = pendingDict[ts];
                if (ce != null) lastKnownCE = ce;
                if (pe != null) lastKnownPE = pe;

                if (lastKnownCE != null && lastKnownPE != null)
                {
                    firstCompleteTimestamp = ts;
                    break;
                }
            }

            if (!firstCompleteTimestamp.HasValue)
            {
                // Still waiting for both legs to have at least one bar
                return;
            }

            // Reset for combining pass
            lastKnownCE = null;
            lastKnownPE = null;

            // Second pass: combine bars using forward-fill from the first complete timestamp
            foreach (var ts in allTimestamps)
            {
                var (ce, pe) = pendingDict[ts];

                // Update last known values
                if (ce != null) lastKnownCE = ce;
                if (pe != null) lastKnownPE = pe;

                // Only start combining from first complete timestamp
                if (ts < firstCompleteTimestamp.Value)
                    continue;

                // Use current bar if available, otherwise use last known (forward-fill)
                var ceToUse = ce ?? lastKnownCE;
                var peToUse = pe ?? lastKnownPE;

                if (ceToUse != null && peToUse != null)
                {
                    var combined = new BarData
                    {
                        Open = ceToUse.Open + peToUse.Open,
                        High = ceToUse.High + peToUse.High,   // Approximation
                        Low = ceToUse.Low + peToUse.Low,       // Approximation
                        Close = ceToUse.Close + peToUse.Close,
                        Volume = (ce?.Volume ?? 0) + (pe?.Volume ?? 0) // Only count actual volume
                    };

                    completeBars.Add((ts, combined, ceToUse.Close, peToUse.Close));
                    completedTimestamps.Add(ts);
                }
            }

            if (completeBars.Count == 0)
                return;

            // Remove completed bars from pending
            foreach (var ts in completedTimestamps)
            {
                pendingDict.TryRemove(ts, out _);
            }

            // Save to SQLite
            SaveBarsToDatabase(straddleSymbol, completeBars);

            Logger.Debug($"[StraddleBarCache] Combined {completeBars.Count} bars for {straddleSymbol} (forward-fill from {firstCompleteTimestamp.Value:HH:mm})");
        }

        /// <summary>
        /// Saves combined straddle bars to SQLite database
        /// </summary>
        private void SaveBarsToDatabase(string straddleSymbol, List<(DateTime timestamp, BarData bar, double ceClose, double peClose)> bars)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    using (var transaction = conn.BeginTransaction())
                    {
                        string insertSql = @"
                            INSERT OR REPLACE INTO straddle_bars
                            (straddle_symbol, timestamp, open, high, low, close, volume, ce_close, pe_close)
                            VALUES (@symbol, @timestamp, @open, @high, @low, @close, @volume, @ceClose, @peClose)";

                        using (var cmd = new SQLiteCommand(insertSql, conn))
                        {
                            cmd.Parameters.Add("@symbol", System.Data.DbType.String);
                            cmd.Parameters.Add("@timestamp", System.Data.DbType.String);
                            cmd.Parameters.Add("@open", System.Data.DbType.Double);
                            cmd.Parameters.Add("@high", System.Data.DbType.Double);
                            cmd.Parameters.Add("@low", System.Data.DbType.Double);
                            cmd.Parameters.Add("@close", System.Data.DbType.Double);
                            cmd.Parameters.Add("@volume", System.Data.DbType.Double);
                            cmd.Parameters.Add("@ceClose", System.Data.DbType.Double);
                            cmd.Parameters.Add("@peClose", System.Data.DbType.Double);

                            foreach (var (timestamp, bar, ceClose, peClose) in bars)
                            {
                                cmd.Parameters["@symbol"].Value = straddleSymbol;
                                cmd.Parameters["@timestamp"].Value = timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                                cmd.Parameters["@open"].Value = bar.Open;
                                cmd.Parameters["@high"].Value = bar.High;
                                cmd.Parameters["@low"].Value = bar.Low;
                                cmd.Parameters["@close"].Value = bar.Close;
                                cmd.Parameters["@volume"].Value = bar.Volume;
                                cmd.Parameters["@ceClose"].Value = ceClose;
                                cmd.Parameters["@peClose"].Value = peClose;

                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                }

                Logger.Debug($"[StraddleBarCache] Saved {bars.Count} bars to database for {straddleSymbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to save bars to database: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Retrieves cached straddle bars from SQLite for a given symbol and date range
        /// </summary>
        public List<Record> GetCachedBars(string straddleSymbol, DateTime fromDate, DateTime toDate)
        {
            var records = new List<Record>();

            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    string selectSql = @"
                        SELECT timestamp, open, high, low, close, volume
                        FROM straddle_bars
                        WHERE straddle_symbol = @symbol
                          AND timestamp >= @fromDate
                          AND timestamp <= @toDate
                        ORDER BY timestamp";

                    using (var cmd = new SQLiteCommand(selectSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", straddleSymbol);
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

                Logger.Debug($"[StraddleBarCache] Retrieved {records.Count} cached bars for {straddleSymbol}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to retrieve cached bars: {ex.Message}", ex);
            }

            return records;
        }

        /// <summary>
        /// Checks if cached data exists for a straddle symbol
        /// </summary>
        public bool HasCachedData(string straddleSymbol)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();

                    string countSql = "SELECT COUNT(*) FROM straddle_bars WHERE straddle_symbol = @symbol";

                    using (var cmd = new SQLiteCommand(countSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@symbol", straddleSymbol);
                        var count = Convert.ToInt32(cmd.ExecuteScalar());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to check cached data: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Clears all cached data (useful for fresh session)
        /// </summary>
        public void ClearCache()
        {
            try
            {
                _pendingBars.Clear();

                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand("DELETE FROM straddle_bars", conn))
                    {
                        int deleted = cmd.ExecuteNonQuery();
                        Logger.Info($"[StraddleBarCache] Cleared {deleted} cached bars");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to clear cache: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clears cached data for expired symbols (e.g., yesterday's expiry)
        /// </summary>
        public void ClearExpiredData(DateTime expiryDate)
        {
            try
            {
                string expiryPattern = expiryDate.ToString("yyMMM").ToUpper(); // e.g., "25DEC"

                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    // Keep only bars for current expiry
                    using (var cmd = new SQLiteCommand(
                        "DELETE FROM straddle_bars WHERE straddle_symbol NOT LIKE @pattern", conn))
                    {
                        cmd.Parameters.AddWithValue("@pattern", $"%{expiryPattern}%_STRDL");
                        int deleted = cmd.ExecuteNonQuery();
                        Logger.Info($"[StraddleBarCache] Cleared {deleted} expired bars");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleBarCache] Failed to clear expired data: {ex.Message}", ex);
            }
        }

        private DateTime NormalizeToMinute(DateTime timestamp)
        {
            return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                               timestamp.Hour, timestamp.Minute, 0, timestamp.Kind);
        }

        /// <summary>
        /// Simple bar data structure for pending bars
        /// </summary>
        private class BarData
        {
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public double Volume { get; set; }
        }
    }
}
