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
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Zerodha;
using System.Threading.Tasks.Dataflow;
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
        private static readonly ILoggerService _log = LoggerFactory.GetLogger(LogDomain.MarketData);

        // TPL Dataflow for rate limiting and request sequencing
        private readonly TransformBlock<HistoricalRequest, List<Record>> _requestBlock;
        private const int MAX_CONCURRENT_REQUESTS = 1; // Sequential processing for simplified rate limiting
        private const int RATE_LIMIT_DELAY_MS = 500; // 2 requests per second safety limit
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

            // Initialize the request pipeline with rate limiting
            _requestBlock = new TransformBlock<HistoricalRequest, List<Record>>(
                async request => await ProcessHistoricalRequest(request),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = MAX_CONCURRENT_REQUESTS,
                    BoundedCapacity = 100 // Prevent memory leaks from too many queued requests
                });
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
            _log.Debug($"Getting historical data for {symbol}, period: {barsPeriodType}, market type: {marketType}, dates: {fromDate} to {toDate}");

            // Determine interval string based on BarsPeriodType
            string interval = barsPeriodType == BarsPeriodType.Minute ? "minute" : "day";

            // Enforce 60-day limit for Zerodha API
            DateTime effectiveFromDate = fromDate;
            if ((toDate - fromDate).TotalDays > MAX_DAYS_PER_REQUEST)
            {
                effectiveFromDate = toDate.AddDays(-MAX_DAYS_PER_REQUEST);
                _log.Warn($"Date range exceeds {MAX_DAYS_PER_REQUEST} days for {symbol}. Truncating from {fromDate} to {effectiveFromDate}");
            }

            // CACHE-FIRST: Check SQLite cache before hitting API
            if (barsPeriodType != BarsPeriodType.Tick)
            {
                var cachedRecords = _barCache.GetCachedBars(symbol, interval, effectiveFromDate, toDate);
                if (cachedRecords != null && cachedRecords.Count > 0)
                {
                    _log.Debug($"CACHE HIT: {symbol} - {cachedRecords.Count} bars from cache");
                    return cachedRecords;
                }
            }
            else
            {
                _log.Warn("Tick data not supported for Zerodha historical API");
                return new List<Record>();
            }

            _log.Debug($"CACHE MISS: {symbol} - queueing for Zerodha API via TPL Dataflow");

            // Create a request object for the pipeline
            var request = new HistoricalRequest
            {
                Symbol = symbol,
                Interval = interval,
                FromDate = effectiveFromDate,
                ToDate = toDate
            };

            // Post to the pipeline and wait for result
            // Note: We use SendAsync to respect BoundedCapacity (backpressure)
            if (await _requestBlock.SendAsync(request))
            {
                // In a TransformBlock with MaxDOP=1, we can't easily get the direct result 
                // without matching IDs or using a TaskCompletionSource inside the request.
                // Let's use the TCS pattern for reliability.
                return await request.CompletionSource.Task;
            }

            _log.Error($"Failed to queue historical request for {symbol}");
            return new List<Record>();
        }

        /// <summary>
        /// Process a historical request from the pipeline with rate limiting
        /// </summary>
        private async Task<List<Record>> ProcessHistoricalRequest(HistoricalRequest request)
        {
            try
            {
                _log.Debug($"Processing historical request for {request.Symbol} ({request.FromDate} to {request.ToDate})");

                // Get the instrument token
                long instrumentToken = _instrumentManager.GetInstrumentToken(request.Symbol);
                if (instrumentToken == 0)
                {
                    _log.Error($"Could not find instrument token for {request.Symbol}");
                    request.CompletionSource.SetResult(new List<Record>());
                    return new List<Record>();
                }

                // Apply simple but effective rate limiting delay
                await Task.Delay(RATE_LIMIT_DELAY_MS);

                string fromDateStr = request.FromDate.ToString("yyyy-MM-dd HH:mm:ss");
                string toDateStr = request.ToDate.ToString("yyyy-MM-dd HH:mm:ss");

                // Get historical data from Zerodha
                var records = await GetHistoricalDataChunk(instrumentToken, request.Interval, fromDateStr, toDateStr);

                // Store in cache for future requests
                if (records.Count > 0)
                {
                    _barCache.StoreBars(request.Symbol, request.Interval, records);
                    _log.Debug($"Cached {records.Count} bars for {request.Symbol}");
                }

                request.CompletionSource.SetResult(records);
                return records;
            }
            catch (Exception ex)
            {
                _log.Error($"Error processing historical request for {request.Symbol}: {ex.Message}");
                request.CompletionSource.SetException(ex);
                return new List<Record>();
            }
        }

        /// <summary>
        /// Data structure for a historical request in the pipeline
        /// </summary>
        private class HistoricalRequest
        {
            public string Symbol { get; set; }
            public string Interval { get; set; }
            public DateTime FromDate { get; set; }
            public DateTime ToDate { get; set; }
            public TaskCompletionSource<List<Record>> CompletionSource { get; } = new TaskCompletionSource<List<Record>>();
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
                    _log.Debug($"Received historical response with length: {content.Length} from {fromDateStr} to {toDateStr}");

                    // Parse the JSON response
                    JObject json = JObject.Parse(content);

                    // Check for data
                    if (json["data"] != null && json["data"]["candles"] != null)
                    {
                        JArray candles = (JArray)json["data"]["candles"];

                        foreach (JArray candle in candles.Cast<JArray>())
                        {
                            var record = ParseCandleData(candle);
                            if (record != null)
                            {
                                records.Add(record);
                            }
                        }
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    _log.Error($"Historical API error: {response.StatusCode}, {errorContent}");
                }
            }

            return records;
        }

        /// <summary>
        /// Parses a single candle from Zerodha's JSON array format.
        /// Zerodha candle format: [timestamp, open, high, low, close, volume]
        /// Timestamp format: "2017-12-15T09:15:00+0530"
        /// </summary>
        /// <param name="candle">The JSON array representing a candle</param>
        /// <returns>A Record object, or null if parsing fails</returns>
        private Record ParseCandleData(JArray candle)
        {
            if (candle == null || candle.Count < 6)
                return null;

            try
            {
                // Parse timestamp using DateTimeHelper for NinjaTrader compatibility
                string timestampStr = candle[0].ToString();
                DateTimeOffset dto = DateTimeOffset.Parse(timestampStr);
                DateTime timestamp = DateTimeHelper.EnsureNinjaTraderDateTime(dto.DateTime);

                return new Record
                {
                    TimeStamp = timestamp,
                    Open = Convert.ToDouble(candle[1]),
                    High = Convert.ToDouble(candle[2]),
                    Low = Convert.ToDouble(candle[3]),
                    Close = Convert.ToDouble(candle[4]),
                    Volume = Convert.ToDouble(candle[5])
                };
            }
            catch (Exception ex)
            {
                _log.Debug($"Error parsing candle data: {ex.Message}");
                return null;
            }
        }
    }
}
