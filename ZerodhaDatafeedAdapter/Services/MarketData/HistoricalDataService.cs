using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Zerodha;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.Services.MarketData
{
    /// <summary>
    /// Service for retrieving historical market data from Zerodha.
    /// Implements cache-first lookup and rate limiting to avoid API throttling.
    /// </summary>
    public class HistoricalDataService
    {
        private static HistoricalDataService _instance;
        private readonly ZerodhaClient _zerodhaClient;
        private readonly InstrumentManager _instrumentManager;
        private readonly HistoricalBarCache _barCache;

        // Rate limiting: Zerodha allows ~3 requests/second for historical API
        // We use 6 concurrent requests with 3 second batch delays
        private static readonly SemaphoreSlim _rateLimitSemaphore = new SemaphoreSlim(6, 6);
        private static DateTime _lastBatchTime = DateTime.MinValue;
        private static readonly object _batchLock = new object();
        private const int BATCH_DELAY_MS = 3000; // 3 seconds between batches
        private const int MAX_DAYS_PER_REQUEST = 60; // Zerodha's limit

        /// <summary>
        /// Gets the singleton instance of the HistoricalDataService
        /// </summary>
        public static HistoricalDataService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new HistoricalDataService();
                return _instance;
            }
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private HistoricalDataService()
        {
            _zerodhaClient = ZerodhaClient.Instance;
            _instrumentManager = InstrumentManager.Instance;
            _barCache = HistoricalBarCache.Instance;
        }

        /// <summary>
        /// Gets historical trades for a symbol.
        /// Checks SQLite cache first, then fetches from Zerodha API with rate limiting.
        /// </summary>
        /// <param name="barsPeriodType">The bars period type</param>
        /// <param name="symbol">The symbol</param>
        /// <param name="fromDate">The start date</param>
        /// <param name="toDate">The end date</param>
        /// <param name="marketType">The market type</param>
        /// <param name="viewModelBase">The view model for progress updates</param>
        /// <returns>A list of historical records</returns>
        public async Task<List<Record>> GetHistoricalTrades(
            BarsPeriodType barsPeriodType,
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            MarketType marketType,
            ViewModelBase viewModelBase)
        {
            // Log request parameters (to file only, not NinjaTrader control panel)
            Logger.Debug($"Getting historical data for {symbol}, period: {barsPeriodType}, market type: {marketType}, dates: {fromDate} to {toDate}");

            // Determine interval string based on BarsPeriodType
            string interval = barsPeriodType == BarsPeriodType.Minute ? "minute" : "day";

            // Enforce 60-day limit for Zerodha API
            DateTime effectiveFromDate = fromDate;
            if ((toDate - fromDate).TotalDays > MAX_DAYS_PER_REQUEST)
            {
                effectiveFromDate = toDate.AddDays(-MAX_DAYS_PER_REQUEST);
                Logger.Warn($"[HistoricalDataService] Date range exceeds {MAX_DAYS_PER_REQUEST} days for {symbol}. Truncating from {fromDate} to {effectiveFromDate}");
            }

            List<Record> records = new List<Record>();

            try
            {
                // For Zerodha, we need to format the request correctly
                if (barsPeriodType != BarsPeriodType.Tick)
                {
                    // CACHE-FIRST: Check SQLite cache before hitting Zerodha API
                    var cachedRecords = _barCache.GetCachedBars(symbol, interval, effectiveFromDate, toDate);
                    if (cachedRecords != null && cachedRecords.Count > 0)
                    {
                        Logger.Debug($"[HistoricalDataService] CACHE HIT: {symbol} - {cachedRecords.Count} bars from cache");
                        return cachedRecords;
                    }

                    Logger.Debug($"[HistoricalDataService] CACHE MISS: {symbol} - fetching from Zerodha API");

                    // Get the instrument token
                    long instrumentToken = await _instrumentManager.GetInstrumentToken(symbol);

                    if (instrumentToken == 0)
                    {
                        NinjaTrader.NinjaScript.NinjaScript.Log($"Error: Could not find instrument token for {symbol}", NinjaTrader.Cbi.LogLevel.Error);
                        return records;
                    }

                    string fromDateStr = effectiveFromDate.ToString("yyyy-MM-dd HH:mm:ss");
                    string toDateStr = toDate.ToString("yyyy-MM-dd HH:mm:ss");

                    // Apply rate limiting before making API call
                    await ApplyRateLimiting(symbol);

                    // Get historical data from Zerodha
                    records = await GetHistoricalDataChunk(instrumentToken, interval, fromDateStr, toDateStr);

                    // Store in cache for future requests
                    if (records.Count > 0)
                    {
                        _barCache.StoreBars(symbol, interval, records);
                        Logger.Debug($"[HistoricalDataService] Cached {records.Count} bars for {symbol}");
                    }
                }
                else
                {
                    // Handle tick data if needed
                    NinjaTrader.NinjaScript.NinjaScript.Log("Tick data not supported for Zerodha", NinjaTrader.Cbi.LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                NinjaTrader.NinjaScript.NinjaScript.Log($"Exception in GetHistoricalTrades: {ex.Message}", NinjaTrader.Cbi.LogLevel.Error);
            }

            Logger.Debug($"Returning {records.Count} historical records for {symbol}");
            return records;
        }

        /// <summary>
        /// Applies rate limiting to avoid Zerodha API throttling.
        /// Uses semaphore for concurrent request limiting and batch delays.
        /// </summary>
        private async Task ApplyRateLimiting(string symbol)
        {
            // Wait for semaphore slot (max 6 concurrent requests)
            await _rateLimitSemaphore.WaitAsync();

            try
            {
                // Check if we need to wait for batch delay
                lock (_batchLock)
                {
                    var timeSinceLastBatch = DateTime.Now - _lastBatchTime;
                    if (timeSinceLastBatch.TotalMilliseconds < BATCH_DELAY_MS)
                    {
                        int delayMs = BATCH_DELAY_MS - (int)timeSinceLastBatch.TotalMilliseconds;
                        Logger.Debug($"[HistoricalDataService] Rate limiting: waiting {delayMs}ms before request for {symbol}");
                        Thread.Sleep(delayMs);
                    }
                    _lastBatchTime = DateTime.Now;
                }
            }
            finally
            {
                _rateLimitSemaphore.Release();
            }
        }

        /// <summary>
        /// Gets a chunk of historical data from Zerodha
        /// </summary>
        /// <param name="instrumentToken">The instrument token</param>
        /// <param name="interval">The interval (day, minute, etc.)</param>
        /// <param name="fromDateStr">The start date string</param>
        /// <param name="toDateStr">The end date string</param>
        /// <returns>A list of historical records</returns>
        private async Task<List<Record>> GetHistoricalDataChunk(long instrumentToken, string interval, string fromDateStr, string toDateStr)
        {
            List<Record> records = new List<Record>();

            using (HttpClient client = _zerodhaClient.CreateAuthorizedClient())
            {
                // Format the URL
                string url = $"https://api.kite.trade/instruments/historical/{instrumentToken}/{interval}?from={fromDateStr}&to={toDateStr}";
                
                // Make the request
                HttpResponseMessage response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    Logger.Debug($"Received historical response with length: {content.Length} from {fromDateStr} to {toDateStr}");

                    // Parse the JSON response
                    JObject json = JObject.Parse(content);

                    // Check for data
                    if (json["data"] != null && json["data"]["candles"] != null)
                    {
                        JArray candles = (JArray)json["data"]["candles"];

                        foreach (JArray candle in candles.Cast<JArray>())
                        {
                            // Zerodha candle format: [timestamp, open, high, low, close, volume]
                            if (candle.Count >= 6)
                            {
                                // Parse timestamp
                                string timestampStr = candle[0].ToString(); // "2017-12-15T09:15:00+0530"
                                DateTime timestamp;

                                // Use DateTimeOffset to properly capture the timezone information
                                DateTimeOffset dto = DateTimeOffset.Parse(timestampStr);
                                timestamp = dto.DateTime;

                                // Explicitly specify this as IST time
                                timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Local);

                                // Create record
                                records.Add(new Record
                                {
                                    TimeStamp = timestamp,
                                    Open = Convert.ToDouble(candle[1]),
                                    High = Convert.ToDouble(candle[2]),
                                    Low = Convert.ToDouble(candle[3]),
                                    Close = Convert.ToDouble(candle[4]),
                                    Volume = Convert.ToDouble(candle[5])
                                });
                            }
                        }
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    NinjaTrader.NinjaScript.NinjaScript.Log($"Error response: {response.StatusCode}, {errorContent}", NinjaTrader.Cbi.LogLevel.Error);
                }
            }

            return records;
        }

        /// <summary>
        /// Converts a Unix timestamp to local time
        /// </summary>
        /// <param name="unixTimestamp">The Unix timestamp</param>
        /// <returns>The local DateTime</returns>
        private DateTime UnixSecondsToLocalTime(int unixTimestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(unixTimestamp).ToLocalTime();
        }
    }
}
