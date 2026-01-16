using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Historical.Providers
{
    public class AccelpixHistoricalDataProvider : IHistoricalDataProvider
    {
        private readonly AccelpixApiClient _apiClient;
        private readonly int _rateLimitDelayMs;
        private bool _isInitialized;

        public string ProviderName => "Accelpix";

        public AccelpixHistoricalDataProvider(AccelpixApiClient apiClient, int rateLimitDelayMs = 200)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _rateLimitDelayMs = rateLimitDelayMs;
            _isInitialized = false;

            HistoricalTickLogger.Info("[AccelpixHistoricalDataProvider] Provider created");
        }

        public Task<bool> InitializeAsync(string apiKey, string apiSecret)
        {
            if (_isInitialized)
            {
                HistoricalTickLogger.Info("[AccelpixHistoricalDataProvider] Already initialized");
                return Task.FromResult(true);
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                HistoricalTickLogger.Error("[AccelpixHistoricalDataProvider] API key is required");
                return Task.FromResult(false);
            }

            _isInitialized = true;
            HistoricalTickLogger.Info($"[AccelpixHistoricalDataProvider] Initialized with API key");
            return Task.FromResult(true);
        }

        public async Task<List<HistoricalCandle>> FetchTickDataAsync(
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1minute")
        {
            if (!_isInitialized)
            {
                HistoricalTickLogger.Error("[AccelpixHistoricalDataProvider] Provider not initialized");
                throw new InvalidOperationException("Provider not initialized. Call InitializeAsync first.");
            }

            if (string.IsNullOrEmpty(symbol))
            {
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            }

            HistoricalTickLogger.Info($"[AccelpixHistoricalDataProvider] Fetching ticks: symbol={symbol}, from={fromDate:yyyy-MM-dd}, to={toDate:yyyy-MM-dd}");

            try
            {
                var allTicks = new List<HistoricalCandle>();
                var currentDate = fromDate;

                while (currentDate <= toDate)
                {
                    HistoricalTickLogger.Info($"[AccelpixHistoricalDataProvider] Fetching {symbol} for {currentDate:yyyy-MM-dd}");

                    var accelpixTicks = await _apiClient.GetTickDataAsync(symbol, currentDate);

                    if (accelpixTicks != null && accelpixTicks.Count > 0)
                    {
                        var candles = AccelpixApiClient.ConvertToHistoricalCandles(accelpixTicks);
                        var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                        int removed = candles.Count - filteredCandles.Count;

                        HistoricalTickLogger.Info($"[AccelpixHistoricalDataProvider] {symbol} {currentDate:yyyy-MM-dd}: Downloaded {accelpixTicks.Count} ticks, {filteredCandles.Count} with volume (removed {removed} zero-volume)");

                        allTicks.AddRange(filteredCandles);
                    }
                    else
                    {
                        HistoricalTickLogger.Warn($"[AccelpixHistoricalDataProvider] {symbol} {currentDate:yyyy-MM-dd}: No data from API");
                    }

                    currentDate = currentDate.AddDays(1);

                    if (currentDate <= toDate)
                    {
                        await Task.Delay(_rateLimitDelayMs);
                    }
                }

                HistoricalTickLogger.Info($"[AccelpixHistoricalDataProvider] Success: {allTicks.Count} ticks fetched for {symbol}");
                return allTicks;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[AccelpixHistoricalDataProvider] ERROR fetching {symbol}: {ex.Message}", ex);
                throw;
            }
        }

        public bool IsRateLimitExceeded()
        {
            return false;
        }
    }
}
