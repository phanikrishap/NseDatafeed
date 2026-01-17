using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TokenGeneratorTest
{
    /// <summary>
    /// ICICI Breeze API Client for Historical Data v2
    /// Supports 1-second granularity data for futures and options
    /// Based on Python SDK implementation
    /// </summary>
    public class BreezeApiClient
    {
        // V1 API for session/customer details
        private const string BASE_URL_V1 = "https://api.icicidirect.com/breezeapi/api/v1/";
        private const string CUSTOMER_DETAILS_URL = "https://api.icicidirect.com/breezeapi/api/v1/customerdetails";

        // V2 API for historical data (different domain!)
        private const string BASE_URL_V2 = "https://breezeapi.icicidirect.com/api/v2/";

        private readonly string _apiKey;
        private readonly string _apiSecret;
        private string _sessionToken;
        private string _base64SessionToken;

        private readonly HttpClient _httpClient;

        // Symbol mapping for ICICI API (same as Python)
        private static readonly Dictionary<string, string> SymbolMap = new Dictionary<string, string>
        {
            { "NIFTY", "NIFTY" },
            { "BANKNIFTY", "CNXBAN" },
            { "FINNIFTY", "NIFFIN" },
            { "MIDCPNIFTY", "NIFMID" },
            { "SENSEX", "BSESEN" }
        };

        // Exchange mapping for symbols
        private static readonly Dictionary<string, string> ExchangeMap = new Dictionary<string, string>
        {
            { "NIFTY", "NFO" },
            { "BANKNIFTY", "NFO" },
            { "FINNIFTY", "NFO" },
            { "MIDCPNIFTY", "NFO" },
            { "SENSEX", "BFO" }
        };

        // Valid intervals for v2 API
        private static readonly HashSet<string> ValidIntervals = new HashSet<string>
        {
            "1second", "1minute", "5minute", "30minute", "1day"
        };

        public BreezeApiClient(string apiKey, string apiSecret)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Generate session using the session token from Python/browser login
        /// Uses WebRequest with reflection to allow body in GET request (.NET Framework workaround)
        /// </summary>
        /// <param name="sessionToken">Session token obtained from ICICI login redirect</param>
        public async Task<bool> GenerateSessionAsync(string sessionToken)
        {
            _sessionToken = sessionToken ?? throw new ArgumentNullException(nameof(sessionToken));

            try
            {
                // Call customer details API to validate and get base64 session token
                var requestBody = new Dictionary<string, string>
                {
                    { "SessionToken", _sessionToken },
                    { "AppKey", _apiKey }
                };
                var bodyJson = JsonConvert.SerializeObject(requestBody);

                // Use WebRequest with reflection hack for GET with body (ICICI API requirement)
                var request = WebRequest.Create(new Uri(CUSTOMER_DETAILS_URL));
                request.ContentType = "application/json";
                request.Method = "GET";

                // Reflection hack to allow body in GET request (.NET Framework specific)
                var type = request.GetType();
                var currentMethod = type.GetProperty("CurrentMethod",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(request);
                var methodType = currentMethod.GetType();
                methodType.GetField("ContentBodyNotAllowed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(currentMethod, false);

                // Write body
                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(bodyJson);
                }

                // Get response
                var response = await Task.Run(() => request.GetResponse());
                string responseContent;
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseContent = reader.ReadToEnd();
                }
                response.Close();

                Console.WriteLine($"Customer Details Response: {responseContent}");

                var jsonResponse = JObject.Parse(responseContent);

                if (jsonResponse["Status"]?.Value<int>() != 200)
                {
                    var error = jsonResponse["Error"]?.ToString() ?? "Unknown error";
                    Console.WriteLine($"Session generation failed: {error}");
                    return false;
                }

                _base64SessionToken = jsonResponse["Success"]?["session_token"]?.ToString();

                if (string.IsNullOrEmpty(_base64SessionToken))
                {
                    Console.WriteLine("No session token in response");
                    return false;
                }

                Console.WriteLine("Session generated successfully");
                Console.WriteLine($"Base64 Session Token: {_base64SessionToken.Substring(0, Math.Min(20, _base64SessionToken.Length))}...");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating session: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set session directly if you already have the base64 session token
        /// </summary>
        public void SetSession(string base64SessionToken)
        {
            _base64SessionToken = base64SessionToken ?? throw new ArgumentNullException(nameof(base64SessionToken));
        }

        /// <summary>
        /// Get historical data v2 - supports 1-second interval
        /// Uses the V2 API at breezeapi.icicidirect.com with query parameters
        /// </summary>
        /// <param name="interval">Interval: 1second, 1minute, 5minute, 30minute, 1day</param>
        /// <param name="fromDate">Start datetime</param>
        /// <param name="toDate">End datetime</param>
        /// <param name="stockCode">Stock code (NIFTY, CNXBAN, etc.)</param>
        /// <param name="exchangeCode">Exchange code (NFO, BFO, NSE, BSE)</param>
        /// <param name="productType">Product type: futures, options, cash</param>
        /// <param name="expiryDate">Expiry date (required for F&O)</param>
        /// <param name="right">Option type: call, put (required for options)</param>
        /// <param name="strikePrice">Strike price (required for options)</param>
        public async Task<HistoricalDataResponse> GetHistoricalDataV2Async(
            string interval,
            DateTime fromDate,
            DateTime toDate,
            string stockCode,
            string exchangeCode,
            string productType = "",
            DateTime? expiryDate = null,
            string right = "",
            string strikePrice = "")
        {
            // Validate interval
            if (!ValidIntervals.Contains(interval.ToLower()))
            {
                return new HistoricalDataResponse
                {
                    Success = false,
                    Error = $"Invalid interval. Must be one of: {string.Join(", ", ValidIntervals)}"
                };
            }

            if (string.IsNullOrEmpty(_base64SessionToken))
            {
                return new HistoricalDataResponse
                {
                    Success = false,
                    Error = "Session not generated. Please call GenerateSessionAsync first."
                };
            }

            try
            {
                // Build query parameters (V2 API uses query params, not body)
                var queryParams = new Dictionary<string, string>
                {
                    { "interval", interval.ToLower() },
                    { "from_date", FormatDateTime(fromDate) },
                    { "to_date", FormatDateTime(toDate) },
                    { "stock_code", stockCode },
                    { "exch_code", exchangeCode.ToUpper() }  // Note: exch_code not exchange_code
                };

                if (!string.IsNullOrEmpty(productType))
                    queryParams["product_type"] = productType.ToLower();

                if (expiryDate.HasValue)
                    queryParams["expiry_date"] = FormatExpiryDate(expiryDate.Value);

                if (!string.IsNullOrEmpty(right))
                    queryParams["right"] = right.ToLower();

                if (!string.IsNullOrEmpty(strikePrice))
                    queryParams["strike_price"] = strikePrice;

                // Make request using V2 API
                var response = await MakeV2RequestAsync("historicalcharts", queryParams);

                return ParseHistoricalDataResponse(response);
            }
            catch (Exception ex)
            {
                return new HistoricalDataResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// Get historical data for options with friendly parameters
        /// </summary>
        public async Task<HistoricalDataResponse> GetOptionsHistoricalDataAsync(
            string symbol,
            int strikePrice,
            string optionType,  // CE or PE
            DateTime expiryDate,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1second",
            string explicitExchange = null)  // Optional: explicitly specify exchange (e.g., "BFO" for SENSEX)
        {
            // Map symbol to stock code (use as-is if not in map)
            string stockCode = SymbolMap.ContainsKey(symbol.ToUpper())
                ? SymbolMap[symbol.ToUpper()]
                : symbol;

            // Get exchange code - use explicit exchange if provided, otherwise lookup or default to NFO
            string exchangeCode;
            if (!string.IsNullOrEmpty(explicitExchange))
            {
                exchangeCode = explicitExchange;
            }
            else if (ExchangeMap.ContainsKey(symbol.ToUpper()))
            {
                exchangeCode = ExchangeMap[symbol.ToUpper()];
            }
            else
            {
                exchangeCode = "NFO";
            }

            // Map option type
            string right = optionType.ToUpper() == "CE" ? "call" : "put";

            return await GetHistoricalDataV2Async(
                interval: interval,
                fromDate: fromDate,
                toDate: toDate,
                stockCode: stockCode,
                exchangeCode: exchangeCode,
                productType: "options",
                expiryDate: expiryDate,
                right: right,
                strikePrice: strikePrice.ToString()
            );
        }

        /// <summary>
        /// Get historical data for futures
        /// </summary>
        public async Task<HistoricalDataResponse> GetFuturesHistoricalDataAsync(
            string symbol,
            DateTime expiryDate,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1second")
        {
            string stockCode = SymbolMap.ContainsKey(symbol.ToUpper())
                ? SymbolMap[symbol.ToUpper()]
                : symbol;

            string exchangeCode = ExchangeMap.ContainsKey(symbol.ToUpper())
                ? ExchangeMap[symbol.ToUpper()]
                : "NFO";

            return await GetHistoricalDataV2Async(
                interval: interval,
                fromDate: fromDate,
                toDate: toDate,
                stockCode: stockCode,
                exchangeCode: exchangeCode,
                productType: "futures",
                expiryDate: expiryDate
            );
        }

        /// <summary>
        /// Get historical data for cash/spot
        /// </summary>
        public async Task<HistoricalDataResponse> GetSpotHistoricalDataAsync(
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1day",
            string exchangeCode = "NSE")
        {
            string stockCode = SymbolMap.ContainsKey(symbol.ToUpper())
                ? SymbolMap[symbol.ToUpper()]
                : symbol;

            return await GetHistoricalDataV2Async(
                interval: interval,
                fromDate: fromDate,
                toDate: toDate,
                stockCode: stockCode,
                exchangeCode: exchangeCode,
                productType: "cash"
            );
        }

        /// <summary>
        /// Download 1-second data in chunks (max 999 seconds per call)
        /// </summary>
        public async Task<List<HistoricalCandle>> DownloadSecondDataInChunksAsync(
            string symbol,
            int strikePrice,
            string optionType,
            DateTime expiryDate,
            DateTime fromDate,
            DateTime toDate,
            int delayMs = 1000)
        {
            const int MAX_SECONDS_PER_CALL = 999;
            var allData = new List<HistoricalCandle>();
            var currentStart = fromDate;

            int chunkCount = 0;
            while (currentStart < toDate)
            {
                var chunkEnd = currentStart.AddSeconds(MAX_SECONDS_PER_CALL);
                if (chunkEnd > toDate)
                    chunkEnd = toDate;

                Console.WriteLine($"Downloading chunk {++chunkCount}: {currentStart:HH:mm:ss} to {chunkEnd:HH:mm:ss}");

                var response = await GetOptionsHistoricalDataAsync(
                    symbol, strikePrice, optionType, expiryDate,
                    currentStart, chunkEnd, "1second");

                if (response.Success && response.Data != null)
                {
                    allData.AddRange(response.Data);
                    Console.WriteLine($"  Got {response.Data.Count} records");
                }
                else
                {
                    Console.WriteLine($"  Error: {response.Error}");
                }

                currentStart = chunkEnd;

                // Rate limiting
                if (currentStart < toDate)
                    await Task.Delay(delayMs);
            }

            return allData;
        }

        #region Private Methods

        /// <summary>
        /// Make request to V2 API (breezeapi.icicidirect.com) using query parameters
        /// This is the correct method for historical data v2
        /// </summary>
        private async Task<string> MakeV2RequestAsync(string endpoint, Dictionary<string, string> queryParams)
        {
            // Build URL with query parameters
            var queryString = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            var url = BASE_URL_V2 + endpoint + "?" + queryString;

            Console.WriteLine($"    [DEBUG] V2 URL: {url}");

            var request = WebRequest.Create(new Uri(url)) as HttpWebRequest;
            request.Method = "GET";
            request.ContentType = "application/json";
            request.Accept = "application/json";

            // V2 API uses different headers - just apikey and X-SessionToken
            request.Headers.Add("apikey", _apiKey);
            request.Headers.Add("X-SessionToken", _base64SessionToken);

            Console.WriteLine($"    [DEBUG] Headers: apikey={_apiKey.Substring(0, 8)}..., X-SessionToken={_base64SessionToken.Substring(0, 15)}...");

            try
            {
                var response = await Task.Run(() => request.GetResponse());
                string responseContent;
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseContent = reader.ReadToEnd();
                }
                response.Close();

                Console.WriteLine($"    [DEBUG] Response (first 500 chars): {responseContent.Substring(0, Math.Min(500, responseContent.Length))}");
                return responseContent;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var errorStream = wex.Response.GetResponseStream())
                    using (var reader = new StreamReader(errorStream, Encoding.UTF8))
                    {
                        var errorContent = reader.ReadToEnd();
                        Console.WriteLine($"    [DEBUG] Error Response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                        return errorContent;
                    }
                }
                throw;
            }
        }

        private string CalculateChecksum(string timestamp, string bodyJson)
        {
            var data = timestamp + bodyJson + _apiSecret;
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
                var builder = new StringBuilder();
                foreach (var b in bytes)
                    builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private string FormatDateTime(DateTime dt)
        {
            // Format: 2022-08-15T09:15:00.000Z
            // IMPORTANT: The Breeze API expects IST times with 'Z' suffix (not actual UTC conversion)
            // This matches the Python SDK behavior
            return dt.ToString("yyyy-MM-ddTHH:mm:ss.000Z");
        }

        private string FormatExpiryDate(DateTime expiry)
        {
            // Expiry format: 2022-08-25T06:00:00.000Z (6 AM UTC = 11:30 AM IST market close)
            return new DateTime(expiry.Year, expiry.Month, expiry.Day, 6, 0, 0, DateTimeKind.Utc)
                .ToString("yyyy-MM-ddTHH:mm:ss.000Z");
        }

        private HistoricalDataResponse ParseHistoricalDataResponse(string json)
        {
            try
            {
                var jObj = JObject.Parse(json);
                var response = new HistoricalDataResponse();

                var status = jObj["Status"]?.Value<int>() ?? 0;

                if (status != 200)
                {
                    response.Success = false;
                    response.Error = jObj["Error"]?.ToString() ?? "Unknown error";
                    return response;
                }

                response.Success = true;
                response.Data = new List<HistoricalCandle>();

                var successData = jObj["Success"];
                if (successData != null && successData.Type == JTokenType.Array)
                {
                    foreach (var item in successData)
                    {
                        var candle = new HistoricalCandle
                        {
                            DateTime = ParseApiDateTime(item["datetime"]?.ToString()),
                            Open = item["open"]?.Value<decimal>() ?? 0,
                            High = item["high"]?.Value<decimal>() ?? 0,
                            Low = item["low"]?.Value<decimal>() ?? 0,
                            Close = item["close"]?.Value<decimal>() ?? 0,
                            Volume = item["volume"]?.Value<long>() ?? 0,
                            OpenInterest = item["open_interest"]?.Value<long>() ?? 0
                        };
                        response.Data.Add(candle);
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                return new HistoricalDataResponse
                {
                    Success = false,
                    Error = $"Failed to parse response: {ex.Message}"
                };
            }
        }

        private DateTime ParseApiDateTime(string dateTimeStr)
        {
            if (string.IsNullOrEmpty(dateTimeStr))
                return DateTime.MinValue;

            // Try various formats
            if (DateTime.TryParse(dateTimeStr, out DateTime result))
                return result;

            return DateTime.MinValue;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get the stock code for a symbol
        /// </summary>
        public static string GetStockCode(string symbol)
        {
            return SymbolMap.ContainsKey(symbol.ToUpper())
                ? SymbolMap[symbol.ToUpper()]
                : symbol;
        }

        /// <summary>
        /// Get the exchange code for a symbol
        /// </summary>
        public static string GetExchangeCode(string symbol)
        {
            return ExchangeMap.ContainsKey(symbol.ToUpper())
                ? ExchangeMap[symbol.ToUpper()]
                : "NFO";
        }

        #endregion
    }

    /// <summary>
    /// Response from historical data API
    /// </summary>
    public class HistoricalDataResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<HistoricalCandle> Data { get; set; }
    }

    /// <summary>
    /// Single candle/bar of historical data
    /// </summary>
    public class HistoricalCandle
    {
        public DateTime DateTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long Volume { get; set; }
        public long OpenInterest { get; set; }

        public override string ToString()
        {
            return $"{DateTime:yyyy-MM-dd HH:mm:ss} | O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume}";
        }
    }
}
