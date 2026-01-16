using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Historical.Providers
{
    public interface IHistoricalDataProvider
    {
        string ProviderName { get; }
        Task<bool> InitializeAsync(string apiKey, string apiSecret);
        Task<List<HistoricalTick>> FetchTickDataAsync(
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1minute"
        );
        bool IsRateLimitExceeded();
    }
}
