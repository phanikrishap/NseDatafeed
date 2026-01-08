using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Auth;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Service for fetching historical tick data from ICICI Direct Breeze API.
    /// Triggers when ICICI broker becomes available via Rx signal.
    /// Implements center-out strike propagation and parallel fetching.
    /// </summary>
    public class IciciHistoricalTickDataService : IDisposable
    {
        private static readonly Lazy<IciciHistoricalTickDataService> _instance =
            new Lazy<IciciHistoricalTickDataService>(() => new IciciHistoricalTickDataService());

        public static IciciHistoricalTickDataService Instance => _instance.Value;

        #region Constants

        // V2 API for historical data
        private const string BASE_URL_V2 = "https://breezeapi.icicidirect.com/api/v2/";

        // Max records per call (~999 seconds for 1-second data)
        private const int MAX_SECONDS_PER_CALL = 999;

        // Parallel fetch configuration
        private const int PARALLEL_STRIKES = 3;
        private const int RATE_LIMIT_DELAY_MS = 500;

        // Symbol mapping for ICICI API
        private static readonly Dictionary<string, string> SymbolMap = new Dictionary<string, string>
        {
            { "NIFTY", "NIFTY" },
            { "BANKNIFTY", "CNXBAN" },
            { "FINNIFTY", "NIFFIN" },
            { "MIDCPNIFTY", "NIFMID" },
            { "SENSEX", "BSESEN" }
        };

        private static readonly Dictionary<string, string> ExchangeMap = new Dictionary<string, string>
        {
            { "NIFTY", "NFO" },
            { "BANKNIFTY", "NFO" },
            { "FINNIFTY", "NFO" },
            { "MIDCPNIFTY", "NFO" },
            { "SENSEX", "BFO" }
        };

        #endregion

        #region Rx Subjects

        // Per-strike historical data availability signals
        private readonly ConcurrentDictionary<string, BehaviorSubject<StrikeHistoricalDataStatus>> _strikeStatusSubjects
            = new ConcurrentDictionary<string, BehaviorSubject<StrikeHistoricalDataStatus>>();

        // Overall service status
        private readonly BehaviorSubject<HistoricalDataServiceStatus> _serviceStatusSubject;
        private readonly Subject<HistoricalDataDownloadProgress> _progressSubject;

        #endregion

        #region State

        private IDisposable _iciciStatusSubscription;
        private string _apiKey;
        private string _apiSecret;
        private string _base64SessionToken;
        private bool _isInitialized;
        private bool _isDownloading;

        // Strike data cache: key = "{symbol}_{expiry}_{strike}_{optionType}"
        private readonly ConcurrentDictionary<string, List<HistoricalCandle>> _tickDataCache
            = new ConcurrentDictionary<string, List<HistoricalCandle>>();

        // Track which strikes have been downloaded
        private readonly ConcurrentDictionary<string, bool> _downloadedStrikes
            = new ConcurrentDictionary<string, bool>();

        #endregion

        #region Public Observables

        /// <summary>
        /// Observable for service status updates
        /// </summary>
        public IObservable<HistoricalDataServiceStatus> ServiceStatus => _serviceStatusSubject.AsObservable();

        /// <summary>
        /// Observable for download progress updates
        /// </summary>
        public IObservable<HistoricalDataDownloadProgress> DownloadProgress => _progressSubject.AsObservable();

        /// <summary>
        /// Get observable for a specific strike's data status
        /// </summary>
        public IObservable<StrikeHistoricalDataStatus> GetStrikeStatusStream(string strikeKey)
        {
            var subject = _strikeStatusSubjects.GetOrAdd(strikeKey,
                _ => new BehaviorSubject<StrikeHistoricalDataStatus>(
                    new StrikeHistoricalDataStatus { StrikeKey = strikeKey, IsAvailable = false }));
            return subject.AsObservable();
        }

        #endregion

        #region Properties

        public bool IsInitialized => _isInitialized;
        public bool IsDownloading => _isDownloading;

        #endregion

        #region Constructor

        private IciciHistoricalTickDataService()
        {
            _serviceStatusSubject = new BehaviorSubject<HistoricalDataServiceStatus>(
                new HistoricalDataServiceStatus { State = HistoricalDataState.NotInitialized });
            _progressSubject = new Subject<HistoricalDataDownloadProgress>();

            IciciApiLogger.Info("[IciciHistoricalTickDataService] Singleton instance created");
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the service - subscribes to ICICI broker availability
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                IciciApiLogger.Info("[IciciHistoricalTickDataService] Already initialized, skipping");
                return;
            }

            IciciApiLogger.Info("[IciciHistoricalTickDataService] Initializing - subscribing to ICICI broker status");

            // Subscribe to ICICI broker availability
            _iciciStatusSubscription = IciciDirectTokenService.Instance.BrokerStatus
                .Where(status => status.IsAvailable)
                .Take(1) // Only trigger once on first availability
                .Subscribe(OnIciciBrokerAvailable, OnSubscriptionError);

            _isInitialized = true;
            _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
            {
                State = HistoricalDataState.WaitingForBroker,
                Message = "Waiting for ICICI Direct broker to become available"
            });
        }

        private void OnIciciBrokerAvailable(IciciBrokerStatus status)
        {
            IciciApiLogger.Info($"[IciciHistoricalTickDataService] ICICI broker available - SessionKey present: {!string.IsNullOrEmpty(status.SessionKey)}");

            // Load API credentials from config
            LoadCredentials();

            if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
            {
                IciciApiLogger.Error("[IciciHistoricalTickDataService] Missing API credentials");
                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Error,
                    Message = "Missing ICICI API credentials"
                });
                return;
            }

            // Generate base64 session token from session key
            _ = Task.Run(() => GenerateSessionAndNotifyReady(status.SessionKey));
        }

        private async Task GenerateSessionAndNotifyReady(string sessionKey)
        {
            try
            {
                bool success = await GenerateSessionAsync(sessionKey);
                if (success)
                {
                    _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                    {
                        State = HistoricalDataState.Ready,
                        Message = "ICICI Historical Data Service is ready"
                    });
                    IciciApiLogger.Info("[IciciHistoricalTickDataService] Service is READY for historical data requests");
                }
                else
                {
                    _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                    {
                        State = HistoricalDataState.Error,
                        Message = "Failed to generate ICICI session"
                    });
                }
            }
            catch (Exception ex)
            {
                IciciApiLogger.Error($"[IciciHistoricalTickDataService] Session generation error: {ex.Message}");
                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Error,
                    Message = $"Session error: {ex.Message}"
                });
            }
        }

        private void OnSubscriptionError(Exception ex)
        {
            IciciApiLogger.Error($"[IciciHistoricalTickDataService] ICICI status subscription error: {ex.Message}");
            _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
            {
                State = HistoricalDataState.Error,
                Message = $"Subscription error: {ex.Message}"
            });
        }

        private void LoadCredentials()
        {
            try
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Classes.Constants.BaseDataFolder, Classes.Constants.ConfigFileName);

                if (!File.Exists(configPath))
                {
                    IciciApiLogger.Error($"[IciciHistoricalTickDataService] Config file not found: {configPath}");
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);
                var iciciConfig = config["IciciDirect"] as JObject;

                if (iciciConfig != null)
                {
                    _apiKey = iciciConfig["ApiKey"]?.ToString();
                    _apiSecret = iciciConfig["ApiSecret"]?.ToString();
                    IciciApiLogger.Info($"[IciciHistoricalTickDataService] Loaded API credentials (key={_apiKey?.Substring(0, Math.Min(8, _apiKey?.Length ?? 0))}...)");
                }
            }
            catch (Exception ex)
            {
                IciciApiLogger.Error($"[IciciHistoricalTickDataService] Error loading credentials: {ex.Message}");
            }
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Generate session using the session token from ICICI login
        /// </summary>
        private async Task<bool> GenerateSessionAsync(string sessionToken)
        {
            try
            {
                const string CUSTOMER_DETAILS_URL = "https://api.icicidirect.com/breezeapi/api/v1/customerdetails";

                var requestBody = new Dictionary<string, string>
                {
                    { "SessionToken", sessionToken },
                    { "AppKey", _apiKey }
                };
                var bodyJson = JsonConvert.SerializeObject(requestBody);

                // Use WebRequest with reflection hack for GET with body
                var request = WebRequest.Create(new Uri(CUSTOMER_DETAILS_URL));
                request.ContentType = "application/json";
                request.Method = "GET";

                // Reflection hack to allow body in GET request
                var type = request.GetType();
                var currentMethod = type.GetProperty("CurrentMethod",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(request);
                var methodType = currentMethod.GetType();
                methodType.GetField("ContentBodyNotAllowed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(currentMethod, false);

                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(bodyJson);
                }

                var response = await Task.Run(() => request.GetResponse());
                string responseContent;
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseContent = reader.ReadToEnd();
                }
                response.Close();

                var jsonResponse = JObject.Parse(responseContent);

                if (jsonResponse["Status"]?.Value<int>() != 200)
                {
                    var error = jsonResponse["Error"]?.ToString() ?? "Unknown error";
                    IciciApiLogger.LogSessionGeneration(false, error);
                    return false;
                }

                _base64SessionToken = jsonResponse["Success"]?["session_token"]?.ToString();

                if (string.IsNullOrEmpty(_base64SessionToken))
                {
                    IciciApiLogger.LogSessionGeneration(false, "No session token in response");
                    return false;
                }

                IciciApiLogger.LogSessionGeneration(true, "Session generated successfully");
                return true;
            }
            catch (Exception ex)
            {
                IciciApiLogger.Error($"[IciciHistoricalTickDataService] Error generating session: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Historical Data Fetching

        /// <summary>
        /// Download historical tick data for option chain using center-out propagation.
        /// Call this when option chain is generated and ICICI is available.
        /// </summary>
        /// <param name="underlying">Underlying symbol (e.g., "NIFTY")</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="projectedAtmStrike">Projected ATM strike for center-out propagation</param>
        /// <param name="strikes">List of all strikes to download</param>
        /// <param name="historicalDate">Date to fetch history for (defaults to prior working day)</param>
        public async Task DownloadOptionChainHistoryAsync(
            string underlying,
            DateTime expiry,
            int projectedAtmStrike,
            List<int> strikes,
            DateTime? historicalDate = null)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_base64SessionToken))
            {
                IciciApiLogger.Warn("[IciciHistoricalTickDataService] Cannot download - not initialized");
                return;
            }

            if (_isDownloading)
            {
                IciciApiLogger.Warn("[IciciHistoricalTickDataService] Download already in progress");
                return;
            }

            _isDownloading = true;
            _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
            {
                State = HistoricalDataState.Downloading,
                Message = "Downloading historical tick data..."
            });

            try
            {
                // Use prior working day if not specified
                DateTime targetDate = historicalDate ?? HolidayCalendarService.Instance.GetPriorWorkingDay();
                IciciApiLogger.LogBatchStart(underlying, expiry, strikes.Count, PARALLEL_STRIKES);

                // Sort strikes by distance from ATM (center-out)
                var orderedStrikes = strikes
                    .OrderBy(s => Math.Abs(s - projectedAtmStrike))
                    .ToList();

                IciciApiLogger.Info($"[IciciHistoricalTickDataService] Projected ATM={projectedAtmStrike}, Total strikes={orderedStrikes.Count}, TargetDate={targetDate:yyyy-MM-dd}");

                // Process in parallel batches of PARALLEL_STRIKES
                int totalStrikes = orderedStrikes.Count * 2; // CE + PE for each strike
                int completedStrikes = 0;

                for (int i = 0; i < orderedStrikes.Count; i += PARALLEL_STRIKES)
                {
                    var batch = orderedStrikes.Skip(i).Take(PARALLEL_STRIKES).ToList();
                    var tasks = new List<Task>();

                    foreach (var strike in batch)
                    {
                        // Download both CE and PE for each strike
                        tasks.Add(DownloadStrikeDataAsync(underlying, expiry, strike, "CE", targetDate));
                        tasks.Add(DownloadStrikeDataAsync(underlying, expiry, strike, "PE", targetDate));
                    }

                    await Task.WhenAll(tasks);
                    completedStrikes += batch.Count * 2;

                    _progressSubject.OnNext(new HistoricalDataDownloadProgress
                    {
                        TotalStrikes = totalStrikes,
                        CompletedStrikes = completedStrikes,
                        CurrentBatch = batch,
                        PercentComplete = (double)completedStrikes / totalStrikes * 100
                    });

                    // Rate limiting between batches
                    if (i + PARALLEL_STRIKES < orderedStrikes.Count)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Ready,
                    Message = $"Downloaded {completedStrikes} strike options history"
                });

                IciciApiLogger.LogBatchComplete(underlying, expiry, completedStrikes, 0, 0);
            }
            catch (Exception ex)
            {
                IciciApiLogger.Error($"[IciciHistoricalTickDataService] Download error: {ex.Message}");
                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Error,
                    Message = $"Download error: {ex.Message}"
                });
            }
            finally
            {
                _isDownloading = false;
            }
        }

        /// <summary>
        /// Download historical data for a single strike
        /// </summary>
        private async Task DownloadStrikeDataAsync(
            string underlying,
            DateTime expiry,
            int strike,
            string optionType,
            DateTime targetDate)
        {
            string strikeKey = GetStrikeKey(underlying, expiry, strike, optionType);

            // Skip if already downloaded
            if (_downloadedStrikes.ContainsKey(strikeKey))
            {
                IciciApiLogger.Debug($"[IciciHistoricalTickDataService] Skipping {strikeKey} - already downloaded");
                return;
            }

            try
            {
                // Market hours: 9:15 AM to 3:30 PM IST
                // But user requested 15:30 to 11:00 check range for NT8 database
                DateTime fromTime = targetDate.Date.AddHours(9).AddMinutes(15);
                DateTime toTime = targetDate.Date.AddHours(15).AddMinutes(30);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var candles = await DownloadSecondDataInChunksAsync(
                    underlying, strike, optionType, expiry, fromTime, toTime);

                stopwatch.Stop();

                if (candles != null && candles.Count > 0)
                {
                    _tickDataCache[strikeKey] = candles;
                    _downloadedStrikes[strikeKey] = true;

                    // Emit status update for this strike
                    var subject = _strikeStatusSubjects.GetOrAdd(strikeKey,
                        _ => new BehaviorSubject<StrikeHistoricalDataStatus>(
                            new StrikeHistoricalDataStatus { StrikeKey = strikeKey, IsAvailable = false }));

                    subject.OnNext(new StrikeHistoricalDataStatus
                    {
                        StrikeKey = strikeKey,
                        IsAvailable = true,
                        CandleCount = candles.Count,
                        FirstTimestamp = candles.First().DateTime,
                        LastTimestamp = candles.Last().DateTime
                    });

                    IciciApiLogger.LogStrikeComplete(underlying, strike, optionType, candles.Count, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    IciciApiLogger.Warn($"[IciciHistoricalTickDataService] {strikeKey}: No data received");
                    _downloadedStrikes[strikeKey] = true; // Mark as attempted
                }
            }
            catch (Exception ex)
            {
                IciciApiLogger.Error($"[IciciHistoricalTickDataService] Error downloading {strikeKey}: {ex.Message}");
            }
        }

        /// <summary>
        /// Download 1-second data in chunks (max 999 seconds per call)
        /// </summary>
        private async Task<List<HistoricalCandle>> DownloadSecondDataInChunksAsync(
            string symbol,
            int strikePrice,
            string optionType,
            DateTime expiryDate,
            DateTime fromDate,
            DateTime toDate)
        {
            var allData = new List<HistoricalCandle>();
            var currentStart = fromDate;

            while (currentStart < toDate)
            {
                var chunkEnd = currentStart.AddSeconds(MAX_SECONDS_PER_CALL);
                if (chunkEnd > toDate)
                    chunkEnd = toDate;

                var response = await GetOptionsHistoricalDataAsync(
                    symbol, strikePrice, optionType, expiryDate,
                    currentStart, chunkEnd, "1second");

                if (response.Success && response.Data != null)
                {
                    allData.AddRange(response.Data);
                }

                currentStart = chunkEnd;

                // Small delay between chunks
                if (currentStart < toDate)
                    await Task.Delay(100);
            }

            return allData;
        }

        /// <summary>
        /// Get historical data for options with friendly parameters
        /// </summary>
        private async Task<HistoricalDataResponse> GetOptionsHistoricalDataAsync(
            string symbol,
            int strikePrice,
            string optionType,
            DateTime expiryDate,
            DateTime fromDate,
            DateTime toDate,
            string interval = "1second")
        {
            // Map symbol to stock code
            string stockCode = SymbolMap.ContainsKey(symbol.ToUpper())
                ? SymbolMap[symbol.ToUpper()]
                : symbol;

            // Get exchange code
            string exchangeCode = ExchangeMap.ContainsKey(symbol.ToUpper())
                ? ExchangeMap[symbol.ToUpper()]
                : "NFO";

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
        /// Get historical data v2 - supports 1-second interval
        /// </summary>
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
                // Build query parameters
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

        #endregion

        #region Data Access

        /// <summary>
        /// Get cached tick data for a specific strike
        /// </summary>
        public List<HistoricalCandle> GetCachedTickData(string underlying, DateTime expiry, int strike, string optionType)
        {
            string strikeKey = GetStrikeKey(underlying, expiry, strike, optionType);
            return _tickDataCache.TryGetValue(strikeKey, out var data) ? data : null;
        }

        /// <summary>
        /// Check if tick data is available for a strike
        /// </summary>
        public bool HasTickData(string underlying, DateTime expiry, int strike, string optionType)
        {
            string strikeKey = GetStrikeKey(underlying, expiry, strike, optionType);
            return _tickDataCache.ContainsKey(strikeKey);
        }

        /// <summary>
        /// Clear cached data (for memory management)
        /// </summary>
        public void ClearCache()
        {
            _tickDataCache.Clear();
            _downloadedStrikes.Clear();
            IciciApiLogger.Info("[IciciHistoricalTickDataService] Cache cleared");
        }

        #endregion

        #region Helper Methods

        private static string GetStrikeKey(string underlying, DateTime expiry, int strike, string optionType)
        {
            return $"{underlying}_{expiry:yyMMdd}_{strike}_{optionType}";
        }

        private string FormatDateTime(DateTime dt)
        {
            // Format: 2022-08-15T09:15:00.000Z (IST time with Z suffix as per Breeze API)
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

            if (DateTime.TryParse(dateTimeStr, out DateTime result))
                return result;

            return DateTime.MinValue;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _iciciStatusSubscription?.Dispose();
            _serviceStatusSubject?.Dispose();
            _progressSubject?.Dispose();

            foreach (var subject in _strikeStatusSubjects.Values)
            {
                subject?.Dispose();
            }
            _strikeStatusSubjects.Clear();

            IciciApiLogger.Info("[IciciHistoricalTickDataService] Disposed");
        }

        #endregion
    }

    #region Models

    /// <summary>
    /// State of the historical data service
    /// </summary>
    public enum HistoricalDataState
    {
        NotInitialized,
        WaitingForBroker,
        Ready,
        Downloading,
        Error
    }

    /// <summary>
    /// Overall service status
    /// </summary>
    public class HistoricalDataServiceStatus
    {
        public HistoricalDataState State { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Download progress update
    /// </summary>
    public class HistoricalDataDownloadProgress
    {
        public int TotalStrikes { get; set; }
        public int CompletedStrikes { get; set; }
        public List<int> CurrentBatch { get; set; }
        public double PercentComplete { get; set; }
    }

    /// <summary>
    /// Per-strike historical data status
    /// </summary>
    public class StrikeHistoricalDataStatus
    {
        public string StrikeKey { get; set; }
        public bool IsAvailable { get; set; }
        public int CandleCount { get; set; }
        public DateTime FirstTimestamp { get; set; }
        public DateTime LastTimestamp { get; set; }
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

    #endregion
}
