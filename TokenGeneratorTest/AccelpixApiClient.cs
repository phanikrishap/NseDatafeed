using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TokenGeneratorTest
{
    /// <summary>
    /// Simple HTTP REST client for Accelpix API
    /// Focused on historical tick data download
    /// </summary>
    public class AccelpixApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _baseUrl;

        public AccelpixApiClient(string apiKey, string baseUrl = "https://apidata.accelpix.in")
        {
            _apiKey = Uri.EscapeDataString(apiKey);
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // Long timeout for large data
        }

        /// <summary>
        /// Get symbol master data (all instruments)
        /// </summary>
        public async Task<string> GetMasterDataAsync(bool includeLotSize = true)
        {
            // Masters endpoint: /api/hsd/Masters/2 (without lot) or /api/hsd/Masters/3 (with lot)
            int version = includeLotSize ? 3 : 2;
            string url = $"{_baseUrl}/api/hsd/Masters/{version}?fmt=json";

            Console.WriteLine($"  Fetching master data from: {url}");
            var response = await _httpClient.GetStringAsync(url);
            return response;
        }

        /// <summary>
        /// Get master data with API token authentication
        /// </summary>
        public async Task<string> GetMasterDataWithAuthAsync()
        {
            string url = $"{_baseUrl}/api/fda/rest/master?api_token={_apiKey}";
            Console.WriteLine($"  Fetching authenticated master data...");
            var response = await _httpClient.GetStringAsync(url);
            return response;
        }

        /// <summary>
        /// Get EOD (End of Day) data for a symbol
        /// Format: GET {base}/api/fda/rest/{ticker}/{startDate}/{endDate}?api_token={token}
        /// Date format: yyyyMMdd
        /// </summary>
        public async Task<List<EodData>> GetEodDataAsync(string ticker, DateTime startDate, DateTime endDate)
        {
            string start = startDate.ToString("yyyyMMdd");
            string end = endDate.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/{Uri.EscapeDataString(ticker)}/{start}/{end}?api_token={_apiKey}";

            Console.WriteLine($"  Fetching EOD data: {ticker} from {start} to {end}");
            var response = await _httpClient.GetStringAsync(url);

            if (string.IsNullOrEmpty(response) || response == "[]")
                return new List<EodData>();

            return JsonConvert.DeserializeObject<List<EodData>>(response) ?? new List<EodData>();
        }

        /// <summary>
        /// Get quotes for multiple symbols
        /// POST {base}/api/fda/rest/quote?api_token={token}
        /// Body: JSON array of ticker strings
        /// </summary>
        public async Task<string> GetQuotesAsync(List<string> symbols)
        {
            string url = $"{_baseUrl}/api/fda/rest/quote?api_token={_apiKey}";
            var jsonContent = JsonConvert.SerializeObject(symbols);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            Console.WriteLine($"  Fetching quotes for {symbols.Count} symbols...");
            var response = await _httpClient.PostAsync(url, content);
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Get tick data for a symbol on a specific date
        /// Endpoint: GET {base}/api/fda/rest/ticks/{ticker}/{date}?api_token={token}
        /// Date format: yyyyMMdd
        /// </summary>
        public async Task<List<TickData>> GetTickDataAsync(string ticker, DateTime date)
        {
            string dateStr = date.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/ticks/{Uri.EscapeDataString(ticker)}/{dateStr}?api_token={_apiKey}";

            Console.WriteLine($"  Fetching tick data: {ticker} for {dateStr}");
            try
            {
                var response = await _httpClient.GetStringAsync(url);

                if (string.IsNullOrEmpty(response) || response == "[]")
                    return new List<TickData>();

                return JsonConvert.DeserializeObject<List<TickData>>(response) ?? new List<TickData>();
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"  Tick data endpoint failed: {ex.Message}");
                return new List<TickData>();
            }
        }

        /// <summary>
        /// Get tick data as raw JSON string (for inspection)
        /// </summary>
        public async Task<string> GetTickDataRawAsync(string ticker, DateTime date)
        {
            string dateStr = date.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/ticks/{Uri.EscapeDataString(ticker)}/{dateStr}?api_token={_apiKey}";

            Console.WriteLine($"  Fetching tick data (raw): {ticker} for {dateStr}");
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"  Tick data endpoint failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get tick data for multiple days
        /// </summary>
        public async Task<Dictionary<DateTime, List<TickData>>> GetTickDataRangeAsync(string ticker, DateTime startDate, DateTime endDate)
        {
            var result = new Dictionary<DateTime, List<TickData>>();

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
        /// Get intraday EOD data (candles) for a symbol
        /// Pattern: GET {base}/api/fda/rest/intraeod/{ticker}/{startDate}/{endDate}/{resolution}?api_token={token}
        /// </summary>
        public async Task<string> GetIntraEodAsync(string ticker, DateTime startDate, DateTime endDate, string resolution = "1")
        {
            string start = startDate.ToString("yyyyMMdd");
            string end = endDate.ToString("yyyyMMdd");
            string url = $"{_baseUrl}/api/fda/rest/intraeod/{Uri.EscapeDataString(ticker)}/{start}/{end}/{resolution}?api_token={_apiKey}";

            Console.WriteLine($"  Fetching IntraEOD: {ticker} from {start} to {end}, resolution: {resolution}");
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                return response;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"  IntraEOD endpoint failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to discover tick data endpoints by testing various URL patterns
        /// </summary>
        public async Task<Dictionary<string, string>> DiscoverTickEndpointsAsync(string ticker, DateTime date)
        {
            var results = new Dictionary<string, string>();
            string dateStr = date.ToString("yyyyMMdd");

            // Various endpoint patterns to try
            var endpoints = new[]
            {
                $"/api/fda/rest/ticks/{ticker}/{dateStr}?api_token={_apiKey}",
                $"/api/fda/rest/tick/{ticker}/{dateStr}?api_token={_apiKey}",
                $"/api/fda/rest/backticks/{ticker}?lastdt={dateStr}%2009:15:00&api_token={_apiKey}",
                $"/api/fda/rest/history/{ticker}/{dateStr}?api_token={_apiKey}",
                $"/api/fda/rest/intraday/{ticker}/{dateStr}?api_token={_apiKey}",
                $"/api/hsd/ticks/{ticker}/{dateStr}?api_token={_apiKey}",
                $"/api/hsd/tick/{ticker}/{dateStr}?fmt=json&api_token={_apiKey}",
            };

            foreach (var endpoint in endpoints)
            {
                string url = _baseUrl + endpoint;
                Console.WriteLine($"  Trying: {endpoint.Substring(0, Math.Min(80, endpoint.Length))}...");

                try
                {
                    var response = await _httpClient.GetAsync(url);
                    var content = await response.Content.ReadAsStringAsync();
                    var status = $"{(int)response.StatusCode} {response.StatusCode}";

                    // Truncate response for display
                    var preview = content.Length > 200 ? content.Substring(0, 200) + "..." : content;
                    results[endpoint] = $"[{status}] {preview}";

                    if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(content) && content != "[]" && content != "null")
                    {
                        Console.WriteLine($"    SUCCESS: {status} - {content.Length} bytes");
                    }
                    else
                    {
                        Console.WriteLine($"    {status}");
                    }
                }
                catch (Exception ex)
                {
                    results[endpoint] = $"[ERROR] {ex.Message}";
                    Console.WriteLine($"    ERROR: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Test API connectivity and authentication
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                // Try a simple quote request
                var response = await GetQuotesAsync(new List<string> { "NIFTY-1" });
                Console.WriteLine($"  Connection test response: {response.Substring(0, Math.Min(200, response.Length))}...");
                return !string.IsNullOrEmpty(response) && !response.Contains("error") && !response.Contains("unauthorized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Connection test failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// EOD data model
    /// </summary>
    public class EodData
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
    /// Tick data model (structure may vary based on actual API response)
    /// </summary>
    public class TickData
    {
        [JsonProperty("tkr")]
        public string Ticker { get; set; }

        [JsonProperty("tm")]
        public uint Time { get; set; }  // Epoch seconds from 1980-01-01

        [JsonProperty("pr")]
        public float Price { get; set; }

        [JsonProperty("qty")]
        public uint Quantity { get; set; }

        [JsonProperty("vol")]
        public uint Volume { get; set; }

        [JsonProperty("oi")]
        public uint OpenInterest { get; set; }

        // Convert Time to DateTime (epoch from 1980-01-01)
        private static readonly DateTime Epoch1980 = new DateTime(1980, 1, 1);

        public DateTime GetDateTime()
        {
            return Epoch1980.AddSeconds(Time);
        }

        public override string ToString()
        {
            return $"{Ticker} {GetDateTime():yyyy-MM-dd HH:mm:ss}: P={Price} Q={Quantity} V={Volume} OI={OpenInterest}";
        }
    }

    /// <summary>
    /// Symbol/Instrument master data
    /// </summary>
    public class SymbolMaster
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
