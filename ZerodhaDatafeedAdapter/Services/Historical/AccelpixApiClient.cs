using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// HTTP REST client for Accelpix API.
    /// Focused on historical tick data download for options.
    /// </summary>
    public class AccelpixApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _baseUrl;

        // Epoch for Accelpix tick timestamps (1980-01-01 00:00:00 IST)
        // Accelpix timestamps are IST-based seconds since this epoch
        private static readonly DateTime Epoch1980Ist = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly TimeZoneInfo IstTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        public AccelpixApiClient(string apiKey, string baseUrl = "https://apidata.accelpix.in")
        {
            _apiKey = Uri.EscapeDataString(apiKey);
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // Long timeout for large data
        }

        /// <summary>
        /// Get tick data for a symbol on a specific date.
        /// Endpoint: GET {base}/api/fda/rest/ticks/{ticker}/{date}?api_token={token}
        /// Date format: yyyyMMdd
        /// </summary>
        public async Task<List<AccelpixTickData>> GetTickDataAsync(string ticker, DateTime date)
        {
            string dateStr = date.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/ticks/{Uri.EscapeDataString(ticker)}/{dateStr}?api_token={_apiKey}";

            HistoricalTickLogger.LogRequest("ACCELPIX", $"/api/fda/rest/ticks/{ticker}/{dateStr}", "GET");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var response = await _httpClient.GetStringAsync(url);
                sw.Stop();

                if (string.IsNullOrEmpty(response) || response == "[]")
                {
                    HistoricalTickLogger.LogResponse("ACCELPIX", $"ticks/{ticker}/{dateStr}", true, 0, sw.ElapsedMilliseconds);
                    return new List<AccelpixTickData>();
                }

                var ticks = JsonConvert.DeserializeObject<List<AccelpixTickData>>(response) ?? new List<AccelpixTickData>();
                HistoricalTickLogger.LogResponse("ACCELPIX", $"ticks/{ticker}/{dateStr}", true, ticks.Count, sw.ElapsedMilliseconds);
                return ticks;
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                HistoricalTickLogger.LogApiError("ACCELPIX", $"ticks/{ticker}/{dateStr}", "HTTP", ex.Message, ex);
                return new List<AccelpixTickData>();
            }
            catch (Exception ex)
            {
                sw.Stop();
                HistoricalTickLogger.LogApiError("ACCELPIX", $"ticks/{ticker}/{dateStr}", "PARSE", ex.Message, ex);
                return new List<AccelpixTickData>();
            }
        }

        /// <summary>
        /// Get tick data as raw JSON string (for debugging/inspection).
        /// </summary>
        public async Task<string> GetTickDataRawAsync(string ticker, DateTime date)
        {
            string dateStr = date.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/ticks/{Uri.EscapeDataString(ticker)}/{dateStr}?api_token={_apiKey}";

            HistoricalTickLogger.Debug($"[RAW] Fetching tick data: {ticker} for {dateStr}");

            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex)
            {
                HistoricalTickLogger.Error($"[RAW] Tick data endpoint failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get tick data for multiple days.
        /// </summary>
        public async Task<Dictionary<DateTime, List<AccelpixTickData>>> GetTickDataRangeAsync(string ticker, DateTime startDate, DateTime endDate)
        {
            var result = new Dictionary<DateTime, List<AccelpixTickData>>();

            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                // Skip weekends
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                    continue;

                var ticks = await GetTickDataAsync(ticker, date);
                if (ticks.Count > 0)
                {
                    result[date] = ticks;
                }
            }

            return result;
        }

        /// <summary>
        /// Get quotes for multiple symbols (for testing connectivity).
        /// POST {base}/api/fda/rest/quote?api_token={token}
        /// Body: JSON array of ticker strings
        /// </summary>
        public async Task<string> GetQuotesAsync(List<string> symbols)
        {
            string url = $"{_baseUrl}/api/fda/rest/quote?api_token={_apiKey}";
            var jsonContent = JsonConvert.SerializeObject(symbols);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HistoricalTickLogger.Debug($"[QUOTE] Fetching quotes for {symbols.Count} symbols...");
            var response = await _httpClient.PostAsync(url, content);
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Test API connectivity and authentication.
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var response = await GetQuotesAsync(new List<string> { "NIFTY-1" });
                bool success = !string.IsNullOrEmpty(response) && !response.Contains("error") && !response.Contains("unauthorized");
                HistoricalTickLogger.Info($"[CONNECTION] Test result: {(success ? "SUCCESS" : "FAILED")}");
                return success;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[CONNECTION] Test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Convert Accelpix tick data to HistoricalCandle format for compatibility with existing cache.
        /// Accelpix 'qty' field is CUMULATIVE volume (resets daily), so we calculate delta between consecutive ticks.
        /// Only ticks with delta > 0 (actual trades) are included.
        /// Note: This method expects ticks from a SINGLE trading day. The first tick's qty is the
        /// first trade volume of the day (cumulative starts at 0 at market open).
        /// </summary>
        public static List<HistoricalCandle> ConvertToHistoricalCandles(List<AccelpixTickData> ticks)
        {
            var candles = new List<HistoricalCandle>();

            if (ticks == null || ticks.Count == 0)
                return candles;

            // Sort by time to ensure correct delta calculation
            var sortedTicks = ticks.OrderBy(t => t.Time).ToList();

            // Start at 0 - cumulative volume resets at market open each day
            // First tick's qty IS the first trade volume of the day
            uint previousCumulativeQty = 0;

            foreach (var tick in sortedTicks)
            {
                // Calculate delta volume (difference from previous cumulative)
                long deltaVolume = tick.Quantity - previousCumulativeQty;
                previousCumulativeQty = tick.Quantity;

                // Only include ticks with actual trade volume (delta > 0)
                if (deltaVolume <= 0)
                    continue;

                // Convert epoch time (seconds from 1980-01-01 IST) to DateTime
                // Accelpix timestamps are IST-based, so we convert to UTC for NT8 storage
                DateTime tickTimeIst = Epoch1980Ist.AddSeconds(tick.Time);
                DateTime tickTime = TimeZoneInfo.ConvertTimeToUtc(tickTimeIst, IstTimeZone);

                // Create candle where OHLC all equal the tick price
                // Volume is the delta (actual trade size), not cumulative
                candles.Add(new HistoricalCandle
                {
                    DateTime = tickTime,
                    Open = (decimal)tick.Price,
                    High = (decimal)tick.Price,
                    Low = (decimal)tick.Price,
                    Close = (decimal)tick.Price,
                    Volume = deltaVolume,
                    OpenInterest = tick.OpenInterest
                });
            }

            return candles;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Accelpix tick data model.
    /// </summary>
    public class AccelpixTickData
    {
        [JsonProperty("tkr")]
        public string Ticker { get; set; }

        [JsonProperty("tm")]
        public uint Time { get; set; }  // Epoch seconds from 1980-01-01

        [JsonProperty("pr")]
        public float Price { get; set; }

        [JsonProperty("qt")]
        public uint Quantity { get; set; }  // Cumulative traded quantity (resets daily)

        [JsonProperty("vol")]
        public uint Volume { get; set; }  // Not used by Accelpix (always empty)

        [JsonProperty("oi")]
        public uint OpenInterest { get; set; }

        // Epoch for Accelpix tick timestamps (1980-01-01 IST)
        private static readonly DateTime Epoch1980Ist = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly TimeZoneInfo IstTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        public DateTime GetDateTime()
        {
            // Returns IST time (local Indian time)
            return Epoch1980Ist.AddSeconds(Time);
        }

        public DateTime GetDateTimeUtc()
        {
            // Returns UTC time for NT8 storage
            return TimeZoneInfo.ConvertTimeToUtc(Epoch1980Ist.AddSeconds(Time), IstTimeZone);
        }

        public override string ToString()
        {
            return $"{Ticker} {GetDateTime():yyyy-MM-dd HH:mm:ss}: P={Price} Q={Quantity} V={Volume} OI={OpenInterest}";
        }
    }
}
