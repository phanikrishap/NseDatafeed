using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

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
        private readonly Action<string, string> _logger;

        // Epoch for Accelpix tick timestamps (1980-01-01 00:00:00 IST)
        // Accelpix timestamps are IST-based seconds since this epoch
        private static readonly DateTime Epoch1980Ist = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        private static readonly TimeZoneInfo IstTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        public AccelpixApiClient(string apiKey, string baseUrl = "https://apidata.accelpix.in", Action<string, string> logger = null)
        {
            _apiKey = Uri.EscapeDataString(apiKey);
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // Long timeout for large data
            _logger = logger;
        }

        private void Log(string level, string message)
        {
            _logger?.Invoke(level, message);
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

            Log("DEBUG", $"[ACCELPIX] Requesting ticks for {ticker} on {dateStr}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var response = await _httpClient.GetStringAsync(url);
                sw.Stop();

                if (string.IsNullOrEmpty(response) || response == "[]")
                {
                    Log("DEBUG", $"[ACCELPIX] No ticks found for {ticker} on {dateStr} (took {sw.ElapsedMilliseconds}ms)");
                    return new List<AccelpixTickData>();
                }

                var ticks = JsonConvert.DeserializeObject<List<AccelpixTickData>>(response) ?? new List<AccelpixTickData>();
                Log("DEBUG", $"[ACCELPIX] Received {ticks.Count} ticks for {ticker} on {dateStr} (took {sw.ElapsedMilliseconds}ms)");
                return ticks;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log("ERROR", $"[ACCELPIX] Error fetching ticks for {ticker} on {dateStr}: {ex.Message}");
                return new List<AccelpixTickData>();
            }
        }

        /// <summary>
        /// Get symbol master data (all instruments).
        /// Endpoint: /api/hsd/Masters/3?fmt=json
        /// </summary>
        public async Task<List<AccelpixSymbolMaster>> GetMasterDataAsync(bool includeLotSize = true)
        {
            int version = includeLotSize ? 3 : 2;
            string url = $"{_baseUrl}/api/hsd/Masters/{version}?fmt=json";

            Log("DEBUG", $"[MASTER] Fetching master data from: {url}");
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                if (string.IsNullOrEmpty(response) || response == "[]")
                    return new List<AccelpixSymbolMaster>();

                return JsonConvert.DeserializeObject<List<AccelpixSymbolMaster>>(response) ?? new List<AccelpixSymbolMaster>();
            }
            catch (Exception ex)
            {
                Log("ERROR", $"[MASTER] Failed to fetch master data: {ex.Message}");
                return new List<AccelpixSymbolMaster>();
            }
        }

        /// <summary>
        /// Get EOD (End of Day) data for a symbol.
        /// Format: GET {base}/api/fda/rest/{ticker}/{startDate}/{endDate}?api_token={token}
        /// </summary>
        public async Task<List<AccelpixEodData>> GetEodDataAsync(string ticker, DateTime startDate, DateTime endDate)
        {
            string start = startDate.ToString("yyyyMMdd");
            string end = endDate.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/{Uri.EscapeDataString(ticker)}/{start}/{end}?api_token={_apiKey}";

            Log("DEBUG", $"[EOD] Fetching EOD data: {ticker} from {start} to {end}");
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                if (string.IsNullOrEmpty(response) || response == "[]")
                    return new List<AccelpixEodData>();

                return JsonConvert.DeserializeObject<List<AccelpixEodData>>(response) ?? new List<AccelpixEodData>();
            }
            catch (Exception ex)
            {
                Log("ERROR", $"[EOD] Failed to fetch EOD data: {ex.Message}");
                return new List<AccelpixEodData>();
            }
        }

        /// <summary>
        /// Get intraday EOD data (candles) for a symbol.
        /// Pattern: GET {base}/api/fda/rest/intraeod/{ticker}/{startDate}/{endDate}/{resolution}?api_token={token}
        /// </summary>
        public async Task<string> GetIntraEodAsync(string ticker, DateTime startDate, DateTime endDate, string resolution = "1")
        {
            string start = startDate.ToString("yyyyMMdd");
            string end = endDate.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/intraeod/{Uri.EscapeDataString(ticker)}/{start}/{end}/{resolution}?api_token={_apiKey}";

            Log("DEBUG", $"[INTRAEOD] Fetching IntraEOD: {ticker} from {start} to {end}, resolution: {resolution}");
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"[INTRAEOD] Failed to fetch IntraEOD: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get tick data as raw JSON string (for debugging/inspection).
        /// </summary>
        public async Task<string> GetTickDataRawAsync(string ticker, DateTime date)
        {
            string dateStr = date.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/ticks/{Uri.EscapeDataString(ticker)}/{dateStr}?api_token={_apiKey}";

            Log("DEBUG", $"[RAW] Fetching tick data: {ticker} for {dateStr}");

            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex)
            {
                Log("ERROR", $"[RAW] Tick data endpoint failed: {ex.Message}");
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

            Log("DEBUG", $"[QUOTE] Fetching quotes for {symbols.Count} symbols...");
            try
            {
                var response = await _httpClient.PostAsync(url, content);
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log("ERROR", $"[QUOTE] Failed to fetch quotes: {ex.Message}");
                return string.Empty;
            }
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
                Log("INFO", $"[CONNECTION] Test result: {(success ? "SUCCESS" : "FAILED")}");
                return success;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"[CONNECTION] Test failed: {ex.Message}");
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

        // Accelpix uses either 'qt' or 'qty' for quantity in different endpoints/versions
        [JsonProperty("qt")]
        public uint Quantity { get; set; }  // Cumulative traded quantity (resets daily)

        [JsonProperty("qty")]
        private uint QuantityAlias { set { Quantity = value; } }

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

    /// <summary>
    /// Accelpix EOD data model.
    /// </summary>
    public class AccelpixEodData
    {
        [JsonProperty("tkr")]
        public string Ticker { get; set; }

        [JsonProperty("td")]
        public string TradeDate { get; set; }

        [JsonProperty("o")]
        public decimal Open { get; set; }

        [JsonProperty("h")]
        public decimal High { get; set; }

        [JsonProperty("l")]
        public decimal Low { get; set; }

        [JsonProperty("c")]
        public decimal Close { get; set; }

        [JsonProperty("v")]
        public long Volume { get; set; }

        [JsonProperty("oi")]
        public long OpenInterest { get; set; }

        public override string ToString()
        {
            return $"{Ticker} {TradeDate}: O={Open} H={High} L={Low} C={Close} V={Volume} OI={OpenInterest}";
        }
    }

    /// <summary>
    /// Accelpix symbol/instrument master data.
    /// </summary>
    public class AccelpixSymbolMaster
    {
        [JsonProperty("tkr")]
        public string Ticker { get; set; }

        [JsonProperty("und")]
        public string Underlying { get; set; }

        [JsonProperty("exp")]
        public string ExpiryDate { get; set; }

        [JsonProperty("stk")]
        public decimal StrikePrice { get; set; }

        [JsonProperty("opt")]
        public string OptionType { get; set; }  // CE or PE

        [JsonProperty("seg")]
        public int SegmentId { get; set; }

        [JsonProperty("lot")]
        public int LotSize { get; set; }

        [JsonProperty("tkn")]
        public int Token { get; set; }

        public override string ToString()
        {
            return $"{Ticker} ({Underlying}) Exp={ExpiryDate} Strike={StrikePrice} {OptionType} Lot={LotSize}";
        }
    }
}
