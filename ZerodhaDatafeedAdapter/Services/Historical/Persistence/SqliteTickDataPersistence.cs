using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Historical.Persistence
{
    public class SqliteTickDataPersistence : ITickDataPersistence
    {
        private readonly TickCacheDb _tickCacheDb;

        public SqliteTickDataPersistence()
        {
            _tickCacheDb = TickCacheDb.Instance;
            HistoricalTickLogger.Info("[SqliteTickDataPersistence] Persistence layer created");
        }

        public Task<bool> CacheTicksAsync(string symbol, DateTime tradeDate, List<HistoricalCandle> ticks)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            }

            if (ticks == null || ticks.Count == 0)
            {
                HistoricalTickLogger.Warn($"[SqliteTickDataPersistence] CacheTicks: No ticks to cache for {symbol}");
                return Task.FromResult(false);
            }

            try
            {
                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Caching {ticks.Count} ticks for {symbol} on {tradeDate:yyyy-MM-dd}");

                _tickCacheDb.CacheTicks(symbol, tradeDate, ticks);

                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Successfully cached {ticks.Count} ticks for {symbol}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SqliteTickDataPersistence] ERROR caching ticks for {symbol}: {ex.Message}", ex);
                return Task.FromResult(false);
            }
        }

        public Task<List<HistoricalCandle>> GetCachedTicksAsync(string symbol, DateTime fromDate, DateTime toDate)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            }

            try
            {
                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Retrieving cached ticks for {symbol} from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}");

                var allTicks = new List<HistoricalCandle>();
                var currentDate = fromDate;

                while (currentDate <= toDate)
                {
                    var ticks = _tickCacheDb.GetCachedTicks(symbol, currentDate);
                    if (ticks != null && ticks.Count > 0)
                    {
                        allTicks.AddRange(ticks);
                        HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Found {ticks.Count} cached ticks for {symbol} on {currentDate:yyyy-MM-dd}");
                    }

                    currentDate = currentDate.AddDays(1);
                }

                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Total cached ticks retrieved: {allTicks.Count}");
                return Task.FromResult(allTicks);
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SqliteTickDataPersistence] ERROR retrieving cached ticks for {symbol}: {ex.Message}", ex);
                return Task.FromResult(new List<HistoricalCandle>());
            }
        }

        public Task<bool> HasCachedDataAsync(string symbol, DateTime tradeDate)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            }

            try
            {
                bool hasCached = _tickCacheDb.HasCachedData(symbol, tradeDate);
                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] HasCachedData for {symbol} on {tradeDate:yyyy-MM-dd}: {hasCached}");
                return Task.FromResult(hasCached);
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SqliteTickDataPersistence] ERROR checking cached data for {symbol}: {ex.Message}", ex);
                return Task.FromResult(false);
            }
        }

        public Task PruneOldDataAsync(int retentionDays)
        {
            try
            {
                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Pruning data older than {retentionDays} days");

                _tickCacheDb.PruneOldData(retentionDays);

                HistoricalTickLogger.Info($"[SqliteTickDataPersistence] Successfully pruned old data");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[SqliteTickDataPersistence] ERROR pruning old data: {ex.Message}", ex);
                return Task.CompletedTask;
            }
        }
    }
}
