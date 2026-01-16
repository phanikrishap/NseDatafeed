using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Historical.Persistence
{
    public interface ITickDataPersistence
    {
        Task<bool> CacheTicksAsync(string symbol, DateTime tradeDate, List<HistoricalCandle> ticks);
        Task<List<HistoricalCandle>> GetCachedTicksAsync(string symbol, DateTime fromDate, DateTime toDate);
        Task<bool> HasCachedDataAsync(string symbol, DateTime tradeDate);
        Task PruneOldDataAsync(int retentionDays);
    }
}
