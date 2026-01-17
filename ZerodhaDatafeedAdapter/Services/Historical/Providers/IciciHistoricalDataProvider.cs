using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;

namespace ZerodhaDatafeedAdapter.Services.Historical.Providers
{
    public class IciciHistoricalDataProvider : IHistoricalDataProvider
    {
        private const string BASE_URL_V2 = "https://breezeapi.icicidirect.com/api/v2/";

        private string _apiKey;
        private string _apiSecret;
        private string _base64SessionToken;
        private bool _isInitialized;

        public string ProviderName => "ICICI Direct";

        public IciciHistoricalDataProvider()
        {
            _isInitialized = false;
            HistoricalTickLogger.Info("[IciciHistoricalDataProvider] Provider created");
        }

        public Task<bool> InitializeAsync(string apiKey, string apiSecret)
        {
            if (_isInitialized)
            {
                HistoricalTickLogger.Info("[IciciHistoricalDataProvider] Already initialized");
                return Task.FromResult(true);
            }

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            {
                HistoricalTickLogger.Error("[IciciHistoricalDataProvider] API key and secret are required");
                return Task.FromResult(false);
            }

            _apiKey = apiKey;
            _apiSecret = apiSecret;
            _isInitialized = true;

            HistoricalTickLogger.Info($"[IciciHistoricalDataProvider] Initialized");
            return Task.FromResult(true);
        }

        public void SetSessionToken(string base64SessionToken)
        {
            _base64SessionToken = base64SessionToken;
            HistoricalTickLogger.Info($"[IciciHistoricalDataProvider] Session token set");
        }

        public async Task<List<HistoricalCandle>> FetchTickDataAsync(
            string symbol,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1minute")
        {
            if (!_isInitialized)
            {
                HistoricalTickLogger.Error("[IciciHistoricalDataProvider] Provider not initialized");
                throw new InvalidOperationException("Provider not initialized. Call InitializeAsync first.");
            }

            if (string.IsNullOrEmpty(_base64SessionToken))
            {
                HistoricalTickLogger.Error("[IciciHistoricalDataProvider] Session token not set");
                throw new InvalidOperationException("Session token not set. Call SetSessionToken first.");
            }

            if (string.IsNullOrEmpty(symbol))
            {
                throw new ArgumentException("Symbol cannot be null or empty", nameof(symbol));
            }

            HistoricalTickLogger.Info($"[IciciHistoricalDataProvider] Fetching ticks: symbol={symbol}, from={fromDate:yyyy-MM-dd}, to={toDate:yyyy-MM-dd}");

            try
            {
                var response = await GetHistoricalDataV2Async(
                    interval: interval,
                    fromDate: fromDate,
                    toDate: toDate,
                    stockCode: symbol,
                    exchangeCode: "NFO"
                );

                if (response.Success && response.Data != null)
                {
                    HistoricalTickLogger.Info($"[IciciHistoricalDataProvider] Success: {response.Data.Count} ticks fetched for {symbol}");
                    return response.Data;
                }
                else
                {
                    HistoricalTickLogger.Warn($"[IciciHistoricalDataProvider] No data or error for {symbol}: {response.Error}");
                    return new List<HistoricalCandle>();
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[IciciHistoricalDataProvider] ERROR fetching {symbol}: {ex.Message}", ex);
                throw;
            }
        }

        public bool IsRateLimitExceeded()
        {
            return false;
        }

        private async Task<HistoricalDataResponse> GetHistoricalDataV2Async(
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
            if (string.IsNullOrEmpty(_base64SessionToken))
            {
                return new HistoricalDataResponse
                {
                    Success = false,
                    Error = "Session not initialized"
                };
            }

            try
            {
                var queryParams = new Dictionary<string, string>
                {
                    { "interval", interval.ToLower() },
                    { "from_date", FormatDateTime(fromDate) },
                    { "to_date", FormatDateTime(toDate) },
                    { "stock_code", stockCode },
                    { "exch_code", exchangeCode.ToUpper() }
                };

                if (!string.IsNullOrEmpty(productType))
                    queryParams["product_type"] = productType.ToLower();

                if (expiryDate.HasValue)
                    queryParams["expiry_date"] = FormatExpiryDate(expiryDate.Value);

                if (!string.IsNullOrEmpty(right))
                    queryParams["right"] = right.ToLower();

                if (!string.IsNullOrEmpty(strikePrice))
                    queryParams["strike_price"] = strikePrice;

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

        private async Task<string> MakeV2RequestAsync(string endpoint, Dictionary<string, string> queryParams)
        {
            var queryString = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            var url = BASE_URL_V2 + endpoint + "?" + queryString;

            var request = WebRequest.Create(new Uri(url)) as HttpWebRequest;
            request.Method = "GET";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers.Add("apikey", _apiKey);
            request.Headers.Add("X-SessionToken", _base64SessionToken);

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
                return responseContent;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (var errorStream = wex.Response.GetResponseStream())
                    using (var reader = new StreamReader(errorStream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
                throw;
            }
        }

        private HistoricalDataResponse ParseHistoricalDataResponse(string jsonResponse)
        {
            try
            {
                var json = JObject.Parse(jsonResponse);
                var status = json["Status"]?.ToString();

                if (status == "Success")
                {
                    var successObj = json["Success"] as JObject;
                    var candles = new List<HistoricalCandle>();

                    if (successObj != null)
                    {
                        foreach (var prop in successObj.Properties())
                        {
                            var candleData = prop.Value as JArray;
                            if (candleData != null)
                            {
                                foreach (var item in candleData)
                                {
                                    var candle = new HistoricalCandle
                                    {
                                        DateTime = DateTime.Parse(item[0].ToString()),
                                        Open = decimal.Parse(item[1].ToString()),
                                        High = decimal.Parse(item[2].ToString()),
                                        Low = decimal.Parse(item[3].ToString()),
                                        Close = decimal.Parse(item[4].ToString()),
                                        Volume = long.Parse(item[5].ToString())
                                    };
                                    candles.Add(candle);
                                }
                            }
                        }
                    }

                    return new HistoricalDataResponse
                    {
                        Success = true,
                        Data = candles
                    };
                }
                else
                {
                    var error = json["Error"]?.ToString();
                    return new HistoricalDataResponse
                    {
                        Success = false,
                        Error = error ?? "Unknown error"
                    };
                }
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

        private string FormatDateTime(DateTime dt)
        {
            return dt.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
        }

        private string FormatExpiryDate(DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd") + "T06:00:00.000Z";
        }

        private class HistoricalDataResponse
        {
            public bool Success { get; set; }
            public List<HistoricalCandle> Data { get; set; }
            public string Error { get; set; }
        }
    }
}
