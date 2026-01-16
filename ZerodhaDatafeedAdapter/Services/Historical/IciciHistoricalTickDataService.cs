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
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Auth;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Service for fetching historical tick data from ICICI Direct Breeze API.
    /// Triggers when ICICI broker becomes available via Rx signal.
    /// Implements center-out strike propagation and parallel fetching.
    /// Implements IHistoricalTickDataSource for unified tick data access.
    /// </summary>
    public class HistoricalTickDataService : BaseHistoricalTickDataService
    {
        private static readonly Lazy<HistoricalTickDataService> _instance =
            new Lazy<HistoricalTickDataService>(() => new HistoricalTickDataService());

        public static HistoricalTickDataService Instance => _instance.Value;

        #region Constants

        // V2 API for historical data
        private const string BASE_URL_V2 = "https://breezeapi.icicidirect.com/api/v2/";

        // Max records per call (~999 seconds for 1-second data)
        private const int MAX_SECONDS_PER_CALL = 999;

        // Parallel fetch configuration
        private const int PARALLEL_STRIKES = 4;
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

        // Per-strike historical data availability signals
        private readonly ConcurrentDictionary<string, BehaviorSubject<StrikeHistoricalDataStatus>> _strikeStatusSubjects
            = new ConcurrentDictionary<string, BehaviorSubject<StrikeHistoricalDataStatus>>();

        // Overall service status
        private readonly BehaviorSubject<HistoricalDataServiceStatus> _serviceStatusSubject;
        private readonly Subject<HistoricalDataDownloadProgress> _progressSubject;

        // Request queue for handling requests before service is ready
        // ReplaySubject buffers requests and replays them when subscribed
        private readonly ReplaySubject<HistoricalDownloadRequest> _requestQueue;
        private IDisposable _requestQueueSubscription;
        private readonly object _queueLock = new object();

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

        // Current Zerodha symbol map for NT persistence (set during DownloadOptionChainHistoryAsync)
        private Dictionary<(int strike, string optionType), string> _currentZerodhaSymbolMap;

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

        #region Properties

        public override bool IsInitialized => _isInitialized;
        public bool IsDownloading => _isDownloading;

        /// <summary>
        /// True when service is fully ready to process requests (initialized AND has valid session token)
        /// </summary>
        public override bool IsReady => _isInitialized && !string.IsNullOrEmpty(_base64SessionToken);

        #endregion

        #region Constructor

        private HistoricalTickDataService() : base(bufferSize: 200)
        {
            _serviceStatusSubject = new BehaviorSubject<HistoricalDataServiceStatus>(
                new HistoricalDataServiceStatus { State = HistoricalDataState.NotInitialized });
            _progressSubject = new Subject<HistoricalDataDownloadProgress>();

            // ReplaySubject with buffer size of 10 - stores up to 10 requests until service is ready
            _requestQueue = new ReplaySubject<HistoricalDownloadRequest>(bufferSize: 10);

            HistoricalTickLogger.Info("[HistoricalTickDataService] Singleton instance created");
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the service - subscribes to ICICI broker availability
        /// </summary>
        public override void Initialize()
        {
            if (_isInitialized)
            {
                HistoricalTickLogger.Info("[HistoricalTickDataService] Already initialized, skipping");
                return;
            }

            HistoricalTickLogger.Info("[HistoricalTickDataService] Initializing - subscribing to ICICI broker status");

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
            HistoricalTickLogger.Info($"[HistoricalTickDataService] ICICI broker available - SessionKey present: {!string.IsNullOrEmpty(status.SessionKey)}");

            // Load API credentials from config
            LoadCredentials();

            if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
            {
                HistoricalTickLogger.Error("[HistoricalTickDataService] Missing API credentials");
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
                    HistoricalTickLogger.Info("[HistoricalTickDataService] Service is READY for historical data requests");

                    // Subscribe to the request queue now that we're ready
                    // This will replay any buffered requests
                    SubscribeToRequestQueue();
                    SubscribeToInstrumentQueue();
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
                HistoricalTickLogger.Error($"[HistoricalTickDataService] Session generation error: {ex.Message}");
                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Error,
                    Message = $"Session error: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Subscribes to the request queue and processes any pending/buffered requests.
        /// Called when service becomes ready.
        /// </summary>
        private void SubscribeToRequestQueue()
        {
            lock (_queueLock)
            {
                if (_requestQueueSubscription != null)
                {
                    HistoricalTickLogger.Debug("[HistoricalTickDataService] Already subscribed to request queue");
                    return;
                }

                HistoricalTickLogger.Info("[HistoricalTickDataService] Subscribing to request queue - processing any pending requests");

                _requestQueueSubscription = _requestQueue
                    .Subscribe(
                        request =>
                        {
                            // Process on a background thread to avoid blocking the Rx pipeline
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    HistoricalTickLogger.Info($"[HistoricalTickDataService] Processing queued request: {request}");
                                    await ProcessDownloadRequestAsync(request);
                                }
                                catch (Exception ex)
                                {
                                    HistoricalTickLogger.Error($"[HistoricalTickDataService] Error processing queued request: {ex.Message}");
                                }
                            });
                        },
                        ex => HistoricalTickLogger.Error($"[HistoricalTickDataService] Request queue error: {ex.Message}"),
                        () => HistoricalTickLogger.Info("[HistoricalTickDataService] Request queue completed")
                    );
            }
        }

        /// <summary>
        /// Subscribes to the per-instrument request queue.
        /// Processes 3 instruments at a time with rate limiting.
        /// Called when service becomes ready.
        /// </summary>
        protected override void SubscribeToInstrumentQueue()
        {
            lock (_queueLock)
            {
                if (_instrumentQueueSubscription != null)
                {
                    HistoricalTickLogger.Debug("[HistoricalTickDataService] Already subscribed to instrument queue");
                    return;
                }

                HistoricalTickLogger.Info("[HistoricalTickDataService] Subscribing to instrument queue - processing 4 at a time with backpressure");

                // Use Concat to ensure sequential batch processing (true backpressure)
                // Each batch waits for previous batch to complete before starting
                _instrumentQueueSubscription = _instrumentRequestQueue
                    .Buffer(PARALLEL_STRIKES) // Batch into groups of 4
                    .Where(batch => batch.Count > 0)
                    .Select(batch => Observable.FromAsync(async () =>
                    {
                        try
                        {
                            HistoricalTickLogger.Info($"[INST-QUEUE] Processing batch of {batch.Count} instruments");

                            // Process all instruments in batch concurrently
                            var tasks = batch.Select(req => ProcessInstrumentRequestAsync(req)).ToList();
                            await Task.WhenAll(tasks);

                            // Rate limit between batches
                            await Task.Delay(RATE_LIMIT_DELAY_MS);

                            return System.Reactive.Unit.Default;
                        }
                        catch (Exception ex)
                        {
                            HistoricalTickLogger.Error($"[INST-QUEUE] Batch processing error: {ex.Message}");
                            return System.Reactive.Unit.Default;
                        }
                    }))
                    .Concat() // Concat ensures sequential execution - only 1 batch in flight at a time
                    .Subscribe(
                        _ => { /* Batch completed */ },
                        ex => HistoricalTickLogger.Error($"[INST-QUEUE] Queue error: {ex.Message}"),
                        () => HistoricalTickLogger.Info("[INST-QUEUE] Queue completed")
                    );
            }
        }

        private void OnSubscriptionError(Exception ex)
        {
            HistoricalTickLogger.Error($"[HistoricalTickDataService] ICICI status subscription error: {ex.Message}");
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
                string configPath = Classes.Constants.GetFolderPath(Classes.Constants.ConfigFileName);

                if (!File.Exists(configPath))
                {
                    HistoricalTickLogger.Error($"[HistoricalTickDataService] Config file not found: {configPath}");
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);
                var iciciConfig = config["IciciDirect"] as JObject;

                if (iciciConfig != null)
                {
                    _apiKey = iciciConfig["ApiKey"]?.ToString();
                    _apiSecret = iciciConfig["ApiSecret"]?.ToString();
                    HistoricalTickLogger.Info($"[HistoricalTickDataService] Loaded API credentials (key={_apiKey?.Substring(0, Math.Min(8, _apiKey?.Length ?? 0))}...)");
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[HistoricalTickDataService] Error loading credentials: {ex.Message}");
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
                    HistoricalTickLogger.LogSessionGeneration(false, error);
                    return false;
                }

                _base64SessionToken = jsonResponse["Success"]?["session_token"]?.ToString();

                if (string.IsNullOrEmpty(_base64SessionToken))
                {
                    HistoricalTickLogger.LogSessionGeneration(false, "No session token in response");
                    return false;
                }

                HistoricalTickLogger.LogSessionGeneration(true, "Session generated successfully");
                return true;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[HistoricalTickDataService] Error generating session: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Request Queue API

        /// <summary>
        /// Queue a download request. If service is ready, processes immediately.
        /// If not ready, the request is buffered and will be processed when service becomes ready.
        /// This is the recommended API for callers - it handles the race condition automatically.
        /// </summary>
        /// <param name="underlying">Underlying symbol (e.g., "NIFTY")</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="projectedAtmStrike">Projected ATM strike for center-out propagation</param>
        /// <param name="strikes">List of all strikes to download</param>
        /// <param name="zerodhaSymbolMap">Optional mapping of (strike,optionType) to Zerodha trading symbol for NT persistence</param>
        /// <param name="historicalDate">Date to fetch history for (defaults to prior working day)</param>
        public void QueueDownloadRequest(
            string underlying,
            DateTime expiry,
            int projectedAtmStrike,
            List<int> strikes,
            Dictionary<(int strike, string optionType), string> zerodhaSymbolMap = null,
            DateTime? historicalDate = null)
        {
            var request = new HistoricalDownloadRequest
            {
                Underlying = underlying,
                Expiry = expiry,
                ProjectedAtmStrike = projectedAtmStrike,
                Strikes = strikes,
                ZerodhaSymbolMap = zerodhaSymbolMap,
                HistoricalDate = historicalDate
            };

            HistoricalTickLogger.Info($"[HistoricalTickDataService] Queueing download request: {request} (IsReady={IsReady})");

            // Push to the ReplaySubject
            // If service is ready and subscribed, it processes immediately
            // If not ready, the request is buffered until subscription happens
            _requestQueue.OnNext(request);
        }

        /// <summary>
        /// Queue a single instrument tick data request from BarsWorker.
        /// This is the API for on-demand tick data requests when cache misses.
        /// Returns an observable that signals when data becomes available.
        /// </summary>
        /// <param name="zerodhaSymbol">Zerodha trading symbol (e.g., SENSEX2610884200CE)</param>
        /// <param name="tradeDate">Date to fetch tick data for</param>
        /// <returns>Observable that signals when tick data is available</returns>
        public override IObservable<InstrumentTickDataStatus> QueueInstrumentTickRequest(string zerodhaSymbol, DateTime tradeDate)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol))
            {
                HistoricalTickLogger.Warn("[INST-QUEUE] Cannot queue null/empty symbol");
                return Observable.Return(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = "Symbol is null or empty"
                });
            }

            // Check if already queued or completed
            var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

            // Check current state
            var currentStatus = statusSubject.Value;
            if (currentStatus.State == TickDataState.Ready)
            {
                HistoricalTickLogger.Debug($"[INST-QUEUE] {zerodhaSymbol} already ready, returning cached status");
                return statusSubject.AsObservable();
            }

            if (currentStatus.State == TickDataState.Downloading)
            {
                HistoricalTickLogger.Debug($"[INST-QUEUE] {zerodhaSymbol} already downloading, returning status stream");
                return statusSubject.AsObservable();
            }

            // Queue the request
            var request = new InstrumentTickDataRequest
            {
                ZerodhaSymbol = zerodhaSymbol,
                TradeDate = tradeDate,
                QueuedAt = DateTime.Now
            };

            HistoricalTickLogger.Info($"[INST-QUEUE] Queueing {zerodhaSymbol} for {tradeDate:yyyy-MM-dd} (IsReady={IsReady})");

            // Update status to queued
            statusSubject.OnNext(new InstrumentTickDataStatus
            {
                ZerodhaSymbol = zerodhaSymbol,
                State = TickDataState.Queued,
                TradeDate = tradeDate
            });

            // Push to the queue - will be buffered if not ready, processed when ready
            _instrumentRequestQueue.OnNext(request);

            return statusSubject.AsObservable();
        }

        /// <summary>
        /// Process a single instrument tick data request.
        /// Downloads from ICICI, caches to SQLite, and signals NT8 to refresh.
        /// Handles market timing scenarios:
        /// - Pre-market (before 9 AM): fetch prior working day only
        /// - Market hours (9:15 AM - 3:30 PM): fetch prior day + current day up to current time
        /// - Post-market (after 3:30 PM): fetch prior day + current day full
        /// </summary>
        private async Task ProcessInstrumentRequestAsync(InstrumentTickDataRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ZerodhaSymbol))
                return;

            string symbol = request.ZerodhaSymbol;

            // Get or create status subject
            var statusSubject = _instrumentStatusSubjects.GetOrAdd(symbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = symbol, State = TickDataState.Pending }));

            try
            {
                // Check if request is stale (older than 10 minutes)
                var age = DateTime.Now - request.QueuedAt;
                if (age.TotalMinutes > 10)
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] Skipping stale request for {symbol} (age={age.TotalMinutes:F1}min)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = symbol,
                        State = TickDataState.Failed,
                        ErrorMessage = "Request expired"
                    });
                    return;
                }

                // Determine which days to fetch based on current IST time
                var daysToFetch = GetDaysToFetch();
                HistoricalTickLogger.Info($"[INST-QUEUE] {symbol}: Market phase={daysToFetch.Phase}, days to fetch: {string.Join(", ", daysToFetch.Dates.Select(d => d.ToString("yyyy-MM-dd")))}");

                // Check if all required days are already in cache
                bool allDaysCached = daysToFetch.Dates.All(d =>
                    IciciTickCacheDb.Instance.HasCachedData(symbol, d));

                if (allDaysCached)
                {
                    // Get total tick count from all cached days
                    int totalTicks = 0;
                    foreach (var date in daysToFetch.Dates)
                    {
                        var cached = IciciTickCacheDb.Instance.GetCachedTicks(symbol, date);
                        if (cached != null) totalTicks += cached.Count;
                    }

                    HistoricalTickLogger.Info($"[INST-QUEUE] CACHE HIT: {symbol} has {totalTicks} ticks across {daysToFetch.Dates.Count} day(s)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = symbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Dates.Last(),
                        TickCount = totalTicks
                    });

                    // Signal NT8 to refresh this instrument's bars
                    await TriggerNT8BarsRefreshAsync(symbol);
                    return;
                }

                // Update status to downloading
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = symbol,
                    State = TickDataState.Downloading,
                    TradeDate = daysToFetch.Dates.LastOrDefault()
                });

                // Parse symbol to extract underlying, expiry, strike, optionType
                var parsedInfo = ParseZerodhaOptionSymbol(symbol);
                if (parsedInfo == null)
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] Cannot parse symbol: {symbol}");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = symbol,
                        State = TickDataState.Failed,
                        ErrorMessage = "Cannot parse symbol"
                    });
                    return;
                }

                HistoricalTickLogger.Info($"[INST-QUEUE] Downloading {symbol}: {parsedInfo.Underlying} {parsedInfo.Strike}{parsedInfo.OptionType} exp={parsedInfo.Expiry:dd-MMM}");

                int totalDownloaded = 0;
                int totalFiltered = 0;

                // Download each day that's not in cache
                foreach (var tradeDate in daysToFetch.Dates)
                {
                    // Skip if this day is already cached
                    if (IciciTickCacheDb.Instance.HasCachedData(symbol, tradeDate))
                    {
                        var cached = IciciTickCacheDb.Instance.GetCachedTicks(symbol, tradeDate);
                        HistoricalTickLogger.Debug($"[INST-QUEUE] {symbol} {tradeDate:yyyy-MM-dd}: Already cached ({cached?.Count ?? 0} ticks)");
                        totalFiltered += cached?.Count ?? 0;
                        continue;
                    }

                    // Determine time range for this day
                    DateTime fromTime = tradeDate.Date.AddHours(9).AddMinutes(15); // 9:15 AM
                    DateTime toTime;

                    // For current day during market hours, fetch up to current time
                    if (tradeDate.Date == DateTime.Today && daysToFetch.Phase == MarketPhase.MarketHours)
                    {
                        var istNow = GetCurrentIstTime();
                        toTime = new DateTime(tradeDate.Year, tradeDate.Month, tradeDate.Day, istNow.Hour, istNow.Minute, istNow.Second);
                        // Ensure we don't go past market close
                        var marketClose = tradeDate.Date.AddHours(15).AddMinutes(30);
                        if (toTime > marketClose) toTime = marketClose;
                    }
                    else
                    {
                        toTime = tradeDate.Date.AddHours(15).AddMinutes(30); // 3:30 PM
                    }

                    HistoricalTickLogger.Info($"[INST-QUEUE] {symbol} {tradeDate:yyyy-MM-dd}: Fetching {fromTime:HH:mm:ss} to {toTime:HH:mm:ss}");

                    // Download ALL 999-second chunks for this day
                    var candles = await DownloadSecondDataInChunksAsync(
                        parsedInfo.Underlying,
                        parsedInfo.Strike,
                        parsedInfo.OptionType,
                        parsedInfo.Expiry,
                        fromTime,
                        toTime);

                    totalDownloaded += candles?.Count ?? 0;

                    if (candles != null && candles.Count > 0)
                    {
                        // Filter zero-volume ticks
                        var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                        int dayFiltered = candles.Count - filteredCandles.Count;

                        HistoricalTickLogger.Info($"[INST-QUEUE] {symbol} {tradeDate:yyyy-MM-dd}: Downloaded {candles.Count} ticks, removed {dayFiltered} zero-volume");

                        if (filteredCandles.Count > 0)
                        {
                            // Cache this day to SQLite (CacheTicks handles replace/insert)
                            IciciTickCacheDb.Instance.CacheTicks(symbol, tradeDate, filteredCandles);
                            totalFiltered += filteredCandles.Count;
                        }
                    }
                    else
                    {
                        HistoricalTickLogger.Warn($"[INST-QUEUE] {symbol} {tradeDate:yyyy-MM-dd}: No data from API");
                    }

                    // Rate limit between days
                    if (daysToFetch.Dates.Count > 1)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                // Final status update
                if (totalFiltered > 0)
                {
                    HistoricalTickLogger.Info($"[INST-QUEUE] {symbol}: COMPLETE - {totalFiltered} ticks across {daysToFetch.Dates.Count} day(s)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = symbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Dates.Last(),
                        TickCount = totalFiltered
                    });

                    // Signal NT8 to refresh this instrument's bars
                    await TriggerNT8BarsRefreshAsync(symbol);
                }
                else
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] {symbol}: No data after filtering (downloaded {totalDownloaded})");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = symbol,
                        State = TickDataState.NoData,
                        TradeDate = daysToFetch.Dates.LastOrDefault(),
                        ErrorMessage = totalDownloaded > 0 ? "All ticks had zero volume" : "No data from ICICI API"
                    });
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[INST-QUEUE] Error processing {symbol}: {ex.Message}");
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = symbol,
                    State = TickDataState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Market phase enumeration for timing-based data fetching
        /// </summary>
        private enum MarketPhase
        {
            PreMarket,    // Before 9:00 AM IST
            MarketHours,  // 9:15 AM - 3:30 PM IST
            PostMarket    // After 3:30 PM IST
        }

        /// <summary>
        /// Result of determining which days to fetch
        /// </summary>
        private class DaysToFetchResult
        {
            public MarketPhase Phase { get; set; }
            public List<DateTime> Dates { get; set; } = new List<DateTime>();
        }

        /// <summary>
        /// Get current time in IST timezone
        /// </summary>
        private DateTime GetCurrentIstTime()
        {
            TimeZoneInfo istZone = TimeZoneInfo.FindSystemTimeZoneById(Classes.Constants.IndianTimeZoneId);
            return TimeZoneInfo.ConvertTime(DateTime.Now, istZone);
        }

        /// <summary>
        /// Determine which days to fetch based on current IST time.
        /// - Pre-market (before 9 AM): fetch prior working day only
        /// - Market hours (9:15 AM - 3:30 PM): fetch prior day + current day
        /// - Post-market (after 3:30 PM): fetch prior day + current day
        /// </summary>
        private DaysToFetchResult GetDaysToFetch()
        {
            var result = new DaysToFetchResult();
            var istNow = GetCurrentIstTime();
            var timeOfDay = istNow.TimeOfDay;

            // Market timing constants
            var preMarketEnd = new TimeSpan(9, 0, 0);       // 9:00 AM
            var marketOpen = new TimeSpan(9, 15, 0);        // 9:15 AM
            var marketClose = new TimeSpan(15, 30, 0);      // 3:30 PM

            // Determine market phase
            if (timeOfDay < preMarketEnd)
            {
                result.Phase = MarketPhase.PreMarket;
            }
            else if (timeOfDay >= marketOpen && timeOfDay <= marketClose)
            {
                result.Phase = MarketPhase.MarketHours;
            }
            else
            {
                result.Phase = MarketPhase.PostMarket;
            }

            // Get prior working day
            DateTime priorWorkingDay = HolidayCalendarService.Instance.GetPriorWorkingDay();
            DateTime today = istNow.Date;

            switch (result.Phase)
            {
                case MarketPhase.PreMarket:
                    // Only fetch prior working day
                    result.Dates.Add(priorWorkingDay);
                    HistoricalTickLogger.Debug($"[GetDaysToFetch] PreMarket ({istNow:HH:mm}): fetching prior day {priorWorkingDay:yyyy-MM-dd}");
                    break;

                case MarketPhase.MarketHours:
                    // Fetch prior day + current day (up to current time)
                    result.Dates.Add(priorWorkingDay);
                    result.Dates.Add(today);
                    HistoricalTickLogger.Debug($"[GetDaysToFetch] MarketHours ({istNow:HH:mm}): fetching prior {priorWorkingDay:yyyy-MM-dd} + today {today:yyyy-MM-dd}");
                    break;

                case MarketPhase.PostMarket:
                    // Fetch prior day + current day (full day)
                    result.Dates.Add(priorWorkingDay);
                    result.Dates.Add(today);
                    HistoricalTickLogger.Debug($"[GetDaysToFetch] PostMarket ({istNow:HH:mm}): fetching prior {priorWorkingDay:yyyy-MM-dd} + today {today:yyyy-MM-dd}");
                    break;
            }

            // Remove duplicates (in case prior working day is today - shouldn't happen but safety)
            result.Dates = result.Dates.Distinct().OrderBy(d => d).ToList();

            return result;
        }

        /// <summary>
        /// Trigger NinjaTrader to refresh/reload bars for an instrument.
        /// This causes NT8 to call BarsWorker again, which will now find the cached data.
        /// </summary>
        private async Task TriggerNT8BarsRefreshAsync(string zerodhaSymbol)
        {
            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                        if (ntInstrument == null)
                        {
                            HistoricalTickLogger.Debug($"[NT8-REFRESH] {zerodhaSymbol}: Instrument not found, skipping refresh");
                            return;
                        }

                        // Force a bars reload by creating a new BarsRequest
                        // This triggers BarsWorker which will now find the cached ICICI data
                        var barsRequest = new BarsRequest(ntInstrument, 1);
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = DateTime.Today.AddHours(9).AddMinutes(15);
                        barsRequest.ToLocal = DateTime.Today.AddHours(15).AddMinutes(30);

                        barsRequest.Request((result, error, msg) =>
                        {
                            if (error == ErrorCode.NoError)
                            {
                                int barsCount = result?.Bars?.Count ?? 0;
                                HistoricalTickLogger.Info($"[NT8-REFRESH] {zerodhaSymbol}: Bars refresh triggered successfully ({barsCount} bars)");

                                // Delete SQLite cache after NT8 has persisted the data
                                if (barsCount > 0)
                                {
                                    Task.Run(() =>
                                    {
                                        int deleted = IciciTickCacheDb.Instance.DeleteCacheForSymbol(zerodhaSymbol);
                                        HistoricalTickLogger.Info($"[NT8-REFRESH] {zerodhaSymbol}: Cleaned up SQLite cache ({deleted} ticks removed)");
                                    });
                                }
                            }
                            else
                            {
                                HistoricalTickLogger.Debug($"[NT8-REFRESH] {zerodhaSymbol}: Refresh completed with {error}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Debug($"[NT8-REFRESH] {zerodhaSymbol}: Exception: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Debug($"[NT8-REFRESH] {zerodhaSymbol}: Dispatcher exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse a Zerodha option symbol to extract underlying, expiry, strike, and option type.
        /// Example: SENSEX2610884200CE -> SENSEX, 2026-10-08, 84200, CE
        /// </summary>
        private OptionSymbolInfo ParseZerodhaOptionSymbol(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return null;

            try
            {
                // Determine option type
                string optionType = null;
                if (symbol.EndsWith("CE"))
                    optionType = "CE";
                else if (symbol.EndsWith("PE"))
                    optionType = "PE";
                else
                    return null;

                // Remove option type suffix
                string remaining = symbol.Substring(0, symbol.Length - 2);

                // Find the underlying (SENSEX, NIFTY, BANKNIFTY, etc.)
                string underlying = null;
                foreach (var known in new[] { "SENSEX", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTY" })
                {
                    if (remaining.StartsWith(known))
                    {
                        underlying = known;
                        remaining = remaining.Substring(known.Length);
                        break;
                    }
                }

                if (underlying == null)
                    return null;

                // Remaining should be YYMMDDSSSSS (date + strike)
                // Date is first 5 chars: YYMDD (e.g., 26108 = 2026-10-08)
                if (remaining.Length < 6)
                    return null;

                string dateStr = remaining.Substring(0, 5);
                string strikeStr = remaining.Substring(5);

                int year = 2000 + int.Parse(dateStr.Substring(0, 2));
                int month = int.Parse(dateStr.Substring(2, 2));
                int day = int.Parse(dateStr.Substring(4, 1));

                // Handle single-digit day (1-9) vs double-digit encoding
                // Actually Zerodha uses YYMDD where DD can be 01-31 encoded as 01-09, 0A-0V (hex-ish)
                // Let's try a simpler approach - parse the full date properly
                // Format is actually YYMDD where M is 1-9,O,N,D for months and D is 01-31
                // This gets complex - let's use a regex approach

                // Simpler: assume format YYMD... where Y=year, M=month code, D=day
                // Month codes: 1-9 for Jan-Sep, O=Oct, N=Nov, D=Dec
                char monthChar = dateStr[2];
                if (monthChar >= '1' && monthChar <= '9')
                    month = monthChar - '0';
                else if (monthChar == 'O')
                    month = 10;
                else if (monthChar == 'N')
                    month = 11;
                else if (monthChar == 'D')
                    month = 12;

                day = int.Parse(dateStr.Substring(3, 2));

                DateTime expiry = new DateTime(year, month, day);
                int strike = int.Parse(strikeStr);

                return new OptionSymbolInfo
                {
                    Underlying = underlying,
                    Expiry = expiry,
                    Strike = strike,
                    OptionType = optionType
                };
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Debug($"[ParseSymbol] Error parsing {symbol}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Process a download request from the queue.
        /// This is the internal handler that executes when a queued request is consumed.
        /// </summary>
        private async Task ProcessDownloadRequestAsync(HistoricalDownloadRequest request)
        {
            if (request == null)
            {
                HistoricalTickLogger.Warn("[HistoricalTickDataService] ProcessDownloadRequestAsync: null request");
                return;
            }

            // Check how old the request is - skip stale requests (older than 5 minutes)
            var age = DateTime.Now - request.QueuedAt;
            if (age.TotalMinutes > 5)
            {
                HistoricalTickLogger.Warn($"[HistoricalTickDataService] Skipping stale request (age={age.TotalMinutes:F1}min): {request}");
                return;
            }

            await DownloadOptionChainHistoryAsync(
                request.Underlying,
                request.Expiry,
                request.ProjectedAtmStrike,
                request.Strikes,
                request.ZerodhaSymbolMap,
                request.HistoricalDate);
        }

        #endregion

        #region Historical Data Fetching

        /// <summary>
        /// Download historical tick data for option chain using center-out propagation.
        /// Starts from projected open strike (accounting for overnight gap) and propagates
        /// simultaneously upward and downward.
        ///
        /// Propagation order:
        /// 1. Center strike CE + PE
        /// 2. Center+1 CE/PE AND Center-1 CE/PE (simultaneously up and down)
        /// 3. Center+2 CE/PE AND Center-2 CE/PE
        /// ... and so on
        ///
        /// NOTE: Prefer using QueueDownloadRequest() which handles race conditions automatically.
        /// </summary>
        /// <param name="underlying">Underlying symbol (e.g., "NIFTY")</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="projectedOpenStrike">Projected open strike (accounts for overnight price action)</param>
        /// <param name="strikes">List of all strikes to download</param>
        /// <param name="zerodhaSymbolMap">Optional mapping of (strike,optionType) to Zerodha trading symbol for NT persistence</param>
        /// <param name="historicalDate">Date to fetch history for (defaults to prior working day)</param>
        public async Task DownloadOptionChainHistoryAsync(
            string underlying,
            DateTime expiry,
            int projectedOpenStrike,
            List<int> strikes,
            Dictionary<(int strike, string optionType), string> zerodhaSymbolMap = null,
            DateTime? historicalDate = null)
        {
            if (!IsReady)
            {
                HistoricalTickLogger.Warn("[HistoricalTickDataService] Cannot download - not ready (use QueueDownloadRequest instead)");
                return;
            }

            if (_isDownloading)
            {
                HistoricalTickLogger.Warn("[HistoricalTickDataService] Download already in progress");
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
                HistoricalTickLogger.LogBatchStart("ICICI", underlying, expiry, strikes.Count, PARALLEL_STRIKES);

                // Build center-out propagation order
                // Start from projected open strike and expand outward (up and down simultaneously)
                var centerOutStrikes = BuildCenterOutStrikeOrder(strikes, projectedOpenStrike);

                HistoricalTickLogger.Info($"[HistoricalTickDataService] Projected Open Strike={projectedOpenStrike}, Total strikes={centerOutStrikes.Count}, TargetDate={targetDate:yyyy-MM-dd}");
                HistoricalTickLogger.Debug($"[CENTER-OUT] First 10 strikes in order: {string.Join(", ", centerOutStrikes.Take(10))}");

                // Store zerodha symbol map for use in DownloadStrikeDataAsync
                _currentZerodhaSymbolMap = zerodhaSymbolMap;

                // Process in parallel batches of PARALLEL_STRIKES
                // Each batch processes N strikes with both CE and PE
                int totalOptions = centerOutStrikes.Count * 2; // CE + PE for each strike
                int completedOptions = 0;

                for (int i = 0; i < centerOutStrikes.Count; i += PARALLEL_STRIKES)
                {
                    var batch = centerOutStrikes.Skip(i).Take(PARALLEL_STRIKES).ToList();
                    var tasks = new List<Task>();

                    foreach (var strike in batch)
                    {
                        // Download both CE and PE for each strike
                        tasks.Add(DownloadStrikeDataAsync(underlying, expiry, strike, "CE", targetDate));
                        tasks.Add(DownloadStrikeDataAsync(underlying, expiry, strike, "PE", targetDate));
                    }

                    await Task.WhenAll(tasks);
                    completedOptions += batch.Count * 2;

                    _progressSubject.OnNext(new HistoricalDataDownloadProgress
                    {
                        TotalStrikes = totalOptions,
                        CompletedStrikes = completedOptions,
                        CurrentBatch = batch,
                        PercentComplete = (double)completedOptions / totalOptions * 100
                    });

                    // Rate limiting between batches
                    if (i + PARALLEL_STRIKES < centerOutStrikes.Count)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                _serviceStatusSubject.OnNext(new HistoricalDataServiceStatus
                {
                    State = HistoricalDataState.Ready,
                    Message = $"Downloaded {completedOptions} strike options history"
                });

                HistoricalTickLogger.LogBatchComplete("ICICI", underlying, expiry, completedOptions, 0, 0);
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[HistoricalTickDataService] Download error: {ex.Message}");
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
        /// Build center-out strike order starting from projected open strike.
        /// Returns strikes in order: center, center+1, center-1, center+2, center-2, ...
        /// This ensures we download the most relevant strikes first (near the projected open).
        /// </summary>
        private List<int> BuildCenterOutStrikeOrder(List<int> allStrikes, int projectedOpenStrike)
        {
            if (allStrikes == null || allStrikes.Count == 0)
                return new List<int>();

            // Sort strikes to find positions
            var sortedStrikes = allStrikes.OrderBy(s => s).ToList();

            // Find the center strike (closest to projected open)
            int centerStrike = sortedStrikes.OrderBy(s => Math.Abs(s - projectedOpenStrike)).First();
            int centerIndex = sortedStrikes.IndexOf(centerStrike);

            var result = new List<int>();

            // Add center strike first
            result.Add(centerStrike);

            // Expand outward - alternate between up and down
            int upIndex = centerIndex + 1;
            int downIndex = centerIndex - 1;

            while (upIndex < sortedStrikes.Count || downIndex >= 0)
            {
                // Add strike above center
                if (upIndex < sortedStrikes.Count)
                {
                    result.Add(sortedStrikes[upIndex]);
                    upIndex++;
                }

                // Add strike below center
                if (downIndex >= 0)
                {
                    result.Add(sortedStrikes[downIndex]);
                    downIndex--;
                }
            }

            return result;
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

            // Skip if already downloaded in this session
            if (_downloadedStrikes.ContainsKey(strikeKey))
            {
                HistoricalTickLogger.Debug($"[HistoricalTickDataService] Skipping {strikeKey} - already downloaded this session");
                return;
            }

            // Get Zerodha symbol for this strike
            string zerodhaSymbol = null;
            if (_currentZerodhaSymbolMap != null)
            {
                _currentZerodhaSymbolMap.TryGetValue((strike, optionType), out zerodhaSymbol);
            }

            if (string.IsNullOrEmpty(zerodhaSymbol))
            {
                // Try to lookup symbol from InstrumentManager
                string segment = underlying == "SENSEX" ? "BFO-OPT" : "NFO-OPT";
                var (token, symbol) = InstrumentManager.Instance.LookupOptionDetails(
                    segment, underlying, expiry.ToString("yyyy-MM-dd"), strike, optionType);
                zerodhaSymbol = symbol;
            }

            // Check if NT8 database already has tick data for this symbol and date
            if (!string.IsNullOrEmpty(zerodhaSymbol))
            {
                bool hasExistingData = await HasTickDataInNT8Async(zerodhaSymbol, targetDate);
                if (hasExistingData)
                {
                    HistoricalTickLogger.Info($"[HistoricalTickDataService] Skipping {strikeKey} - NT8 already has tick data for {targetDate:yyyy-MM-dd}");
                    _downloadedStrikes[strikeKey] = true; // Mark as done
                    return;
                }
            }

            // Check SQLite cache for this symbol and date
            if (!string.IsNullOrEmpty(zerodhaSymbol) && IciciTickCacheDb.Instance.HasCachedData(zerodhaSymbol, targetDate))
            {
                var cachedCandles = IciciTickCacheDb.Instance.GetCachedTicks(zerodhaSymbol, targetDate);
                if (cachedCandles != null && cachedCandles.Count > 0)
                {
                    HistoricalTickLogger.Info($"[HistoricalTickDataService] CACHE HIT: {strikeKey} - using {cachedCandles.Count} cached ticks for {targetDate:yyyy-MM-dd}");
                    _tickDataCache[strikeKey] = cachedCandles;
                    _downloadedStrikes[strikeKey] = true;

                    // Persist cached data to NinjaTrader database
                    await PersistToNinjaTraderDatabaseAsync(zerodhaSymbol, cachedCandles);

                    // Emit status update
                    var subject = _strikeStatusSubjects.GetOrAdd(strikeKey,
                        _ => new BehaviorSubject<StrikeHistoricalDataStatus>(
                            new StrikeHistoricalDataStatus { StrikeKey = strikeKey, IsAvailable = false }));

                    subject.OnNext(new StrikeHistoricalDataStatus
                    {
                        StrikeKey = strikeKey,
                        IsAvailable = true,
                        CandleCount = cachedCandles.Count,
                        FirstTimestamp = cachedCandles.First().DateTime,
                        LastTimestamp = cachedCandles.Last().DateTime,
                        ZerodhaSymbol = zerodhaSymbol
                    });

                    return;
                }
            }

            try
            {
                // Market hours: 9:15 AM to 3:30 PM IST
                DateTime fromTime = targetDate.Date.AddHours(9).AddMinutes(15);
                DateTime toTime = targetDate.Date.AddHours(15).AddMinutes(30);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // CACHE MISS - Call ICICI API
                HistoricalTickLogger.Debug($"[HistoricalTickDataService] CACHE MISS: {strikeKey} - calling ICICI API");
                var candles = await DownloadSecondDataInChunksAsync(
                    underlying, strike, optionType, expiry, fromTime, toTime);

                stopwatch.Stop();

                if (candles != null && candles.Count > 0)
                {
                    // Filter zero-volume ticks before caching
                    var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                    int removedCount = candles.Count - filteredCandles.Count;
                    if (removedCount > 0)
                    {
                        HistoricalTickLogger.Info($"[HistoricalTickDataService] Filtered {removedCount} zero-volume ticks for {strikeKey}");
                    }

                    if (filteredCandles.Count > 0)
                    {
                        _tickDataCache[strikeKey] = filteredCandles;
                        _downloadedStrikes[strikeKey] = true;

                        // Cache to SQLite - BarsWorker will serve this when NT requests tick data
                        if (!string.IsNullOrEmpty(zerodhaSymbol))
                        {
                            IciciTickCacheDb.Instance.CacheTicks(zerodhaSymbol, targetDate, filteredCandles);
                            HistoricalTickLogger.Info($"[HistoricalTickDataService] Cached {filteredCandles.Count} ticks for {zerodhaSymbol} in SQLite");
                        }

                        // Emit status update for this strike
                        var subject = _strikeStatusSubjects.GetOrAdd(strikeKey,
                            _ => new BehaviorSubject<StrikeHistoricalDataStatus>(
                                new StrikeHistoricalDataStatus { StrikeKey = strikeKey, IsAvailable = false }));

                        subject.OnNext(new StrikeHistoricalDataStatus
                        {
                            StrikeKey = strikeKey,
                            IsAvailable = true,
                            CandleCount = filteredCandles.Count,
                            FirstTimestamp = filteredCandles.First().DateTime,
                            LastTimestamp = filteredCandles.Last().DateTime,
                            ZerodhaSymbol = zerodhaSymbol
                        });

                        HistoricalTickLogger.LogStrikeComplete(underlying, strike, optionType, filteredCandles.Count, stopwatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        HistoricalTickLogger.Warn($"[HistoricalTickDataService] {strikeKey}: All ticks filtered out (zero volume)");
                        _downloadedStrikes[strikeKey] = true; // Mark as attempted
                    }
                }
                else
                {
                    HistoricalTickLogger.Warn($"[HistoricalTickDataService] {strikeKey}: No data received from API");
                    _downloadedStrikes[strikeKey] = true; // Mark as attempted
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[HistoricalTickDataService] Error downloading {strikeKey}: {ex.Message}");
            }
        }

        // Retry configuration
        private const int MAX_RETRIES = 2;
        private const int INITIAL_BACKOFF_MS = 500;

        /// <summary>
        /// Download 1-second data in chunks (max 999 seconds per call).
        /// Implements retry with exponential backoff for failed chunks.
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
            int chunkNumber = 0;
            int totalChunks = (int)Math.Ceiling((toDate - fromDate).TotalSeconds / MAX_SECONDS_PER_CALL);
            int failedChunks = 0;
            var failedRanges = new List<string>();

            HistoricalTickLogger.Info($"[CHUNK-DL] Starting download: {symbol} {strikePrice}{optionType}, {totalChunks} chunks from {fromDate:HH:mm:ss} to {toDate:HH:mm:ss}");

            while (currentStart < toDate)
            {
                chunkNumber++;
                var chunkEnd = currentStart.AddSeconds(MAX_SECONDS_PER_CALL);
                if (chunkEnd > toDate)
                    chunkEnd = toDate;

                bool chunkSuccess = false;
                HistoricalDataResponse response = null;

                // Retry loop with exponential backoff
                for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
                {
                    try
                    {
                        if (attempt > 0)
                        {
                            int backoffMs = INITIAL_BACKOFF_MS * (int)Math.Pow(2, attempt - 1);
                            HistoricalTickLogger.Info($"[CHUNK-DL] Retry {attempt}/{MAX_RETRIES} for chunk {chunkNumber}/{totalChunks} after {backoffMs}ms backoff");
                            await Task.Delay(backoffMs);
                        }

                        response = await GetOptionsHistoricalDataAsync(
                            symbol, strikePrice, optionType, expiryDate,
                            currentStart, chunkEnd, "1second");

                        if (response.Success && response.Data != null)
                        {
                            allData.AddRange(response.Data);
                            chunkSuccess = true;

                            if (attempt > 0)
                            {
                                HistoricalTickLogger.Info($"[CHUNK-DL] Chunk {chunkNumber}/{totalChunks} succeeded on retry {attempt}: {response.Data.Count} records");
                            }
                            break; // Success, exit retry loop
                        }
                        else
                        {
                            string errorMsg = response?.Error ?? "Unknown error";
                            HistoricalTickLogger.Warn($"[CHUNK-DL] Chunk {chunkNumber}/{totalChunks} failed (attempt {attempt + 1}): {errorMsg}");

                            // Don't retry on certain errors
                            if (errorMsg.Contains("No data") || errorMsg.Contains("session"))
                            {
                                HistoricalTickLogger.Debug($"[CHUNK-DL] Non-retryable error, skipping remaining retries");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Error($"[CHUNK-DL] Chunk {chunkNumber}/{totalChunks} exception (attempt {attempt + 1}): {ex.Message}");

                        // Continue to retry unless it's a fatal error
                        if (ex is OutOfMemoryException || ex is StackOverflowException)
                            throw;
                    }
                }

                if (!chunkSuccess)
                {
                    failedChunks++;
                    failedRanges.Add($"{currentStart:HH:mm:ss}-{chunkEnd:HH:mm:ss}");
                    HistoricalTickLogger.Error($"[CHUNK-DL] FAILED chunk {chunkNumber}/{totalChunks} ({currentStart:HH:mm:ss}-{chunkEnd:HH:mm:ss}) after {MAX_RETRIES + 1} attempts");
                }

                currentStart = chunkEnd;

                // Small delay between chunks to avoid rate limiting
                if (currentStart < toDate)
                    await Task.Delay(100);
            }

            // Log summary
            if (failedChunks > 0)
            {
                HistoricalTickLogger.Warn($"[CHUNK-DL] Download complete with {failedChunks}/{totalChunks} failed chunks. Missing ranges: {string.Join(", ", failedRanges)}");
            }
            else
            {
                HistoricalTickLogger.Info($"[CHUNK-DL] Download complete: {allData.Count} total records from {totalChunks} chunks");
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

        #region NT Database Check & Persistence

        /// <summary>
        /// Checks if NT8 database already has tick data for the given symbol and date.
        /// Uses BarsRequest to query existing data.
        /// </summary>
        /// <param name="zerodhaSymbol">The Zerodha trading symbol (e.g., SENSEX2610884200CE)</param>
        /// <param name="targetDate">The date to check for tick data</param>
        /// <returns>True if tick data exists for that date, false otherwise</returns>
        public async Task<bool> HasTickDataInNT8Async(string zerodhaSymbol, DateTime targetDate)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol))
            {
                HistoricalTickLogger.Info($"[NT8-CHECK] Symbol is null/empty, returning false");
                return false;
            }

            bool hasData = false;
            bool requestCompleted = false;
            string diagnosticInfo = "";
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            HistoricalTickLogger.Info($"[NT8-CHECK] START checking {zerodhaSymbol} for {targetDate:yyyy-MM-dd}");

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        HistoricalTickLogger.Info($"[NT8-CHECK] Inside dispatcher for {zerodhaSymbol}");

                        var ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                        if (ntInstrument == null)
                        {
                            diagnosticInfo = "Instrument not found in NT8";
                            HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: {diagnosticInfo}");
                            completionSource.TrySetResult(false);
                            return;
                        }

                        HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: Instrument found, FullName={ntInstrument.FullName}, Exchange={ntInstrument.Exchange}");

                        // Create a BarsRequest to check for existing tick data
                        DateTime fromTime = targetDate.Date.AddHours(9).AddMinutes(15);  // 9:15 AM
                        DateTime toTime = targetDate.Date.AddHours(15).AddMinutes(30);   // 3:30 PM

                        HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: Requesting ticks from {fromTime:HH:mm} to {toTime:HH:mm}");

                        var barsRequest = new BarsRequest(ntInstrument, 100); // Request up to 100 bars to check
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = fromTime;
                        barsRequest.ToLocal = toTime;

                        HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: BarsRequest created - BarsPeriodType={barsRequest.BarsPeriod.BarsPeriodType}, Value={barsRequest.BarsPeriod.Value}");

                        barsRequest.Request((barsResult, errorCode, errorMessage) =>
                        {
                            requestCompleted = true;

                            bool hasTicks = false;
                            if (errorCode == ErrorCode.NoError)
                            {
                                if (barsResult != null && barsResult.Bars != null)
                                {
                                    int barCount = barsResult.Bars.Count;
                                    if (barCount > 0)
                                    {
                                        hasTicks = true;
                                        var firstBar = barsResult.Bars.GetTime(0);
                                        var lastBar = barsResult.Bars.GetTime(barCount - 1);
                                        diagnosticInfo = $"FOUND {barCount} ticks ({firstBar:HH:mm:ss} to {lastBar:HH:mm:ss})";
                                    }
                                    else
                                    {
                                        diagnosticInfo = "BarsRequest returned 0 bars";
                                    }
                                }
                                else
                                {
                                    diagnosticInfo = $"BarsResult null={barsResult == null}, Bars null={barsResult?.Bars == null}";
                                }
                            }
                            else
                            {
                                diagnosticInfo = $"ErrorCode={errorCode}, Message={errorMessage}";
                            }

                            HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: Callback - {diagnosticInfo}");
                            completionSource.TrySetResult(hasTicks);
                        });
                    }
                    catch (Exception ex)
                    {
                        diagnosticInfo = $"Exception: {ex.Message}";
                        HistoricalTickLogger.Error($"[NT8-CHECK] {zerodhaSymbol}: {diagnosticInfo}");
                        completionSource.TrySetResult(false);
                    }
                });
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[NT8-CHECK] {zerodhaSymbol}: Dispatcher exception: {ex.Message}");
            }

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(completionSource.Task, timeoutTask);
            if (completedTask == completionSource.Task)
            {
                hasData = completionSource.Task.Result;
            }
            else
            {
                diagnosticInfo = "TIMEOUT - BarsRequest did not complete within 5 seconds";
                HistoricalTickLogger.Warn($"[NT8-CHECK] {zerodhaSymbol}: {diagnosticInfo}");
            }

            HistoricalTickLogger.Info($"[NT8-CHECK] END {zerodhaSymbol}: hasData={hasData}, requestCompleted={requestCompleted}, info={diagnosticInfo}");
            return hasData;
        }

        /// <summary>
        /// Persists historical tick data to NinjaTrader database using the Zerodha symbol name.
        /// Uses NinjaTrader's BarsRequest to write tick data.
        /// </summary>
        private async Task PersistToNinjaTraderDatabaseAsync(string zerodhaSymbol, List<HistoricalCandle> candles)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol) || candles == null || candles.Count == 0)
                return;

            try
            {
                var firstCandle = candles.First();
                var lastCandle = candles.Last();
                HistoricalTickLogger.Info($"[NT8-PERSIST] START {zerodhaSymbol}: {candles.Count} ticks, range={firstCandle.DateTime:yyyy-MM-dd HH:mm:ss} to {lastCandle.DateTime:HH:mm:ss}");

                // Get or create the NT instrument on the UI thread
                Instrument ntInstrument = null;
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                    if (ntInstrument == null)
                    {
                        HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: Instrument not found, attempting to create...");
                        // Try to create it via InstrumentManager
                        var mapping = InstrumentManager.Instance.GetMappingByNtSymbol(zerodhaSymbol);
                        if (mapping != null)
                        {
                            string ntName;
                            NinjaTraderHelper.CreateNTInstrumentFromMapping(mapping, out ntName);
                            ntInstrument = Instrument.GetInstrument(ntName);
                            HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: Created instrument, ntName={ntName}");
                        }
                        else
                        {
                            HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: No mapping found in InstrumentManager");
                        }
                    }
                    else
                    {
                        HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: Instrument found, FullName={ntInstrument.FullName}");
                    }
                });

                if (ntInstrument == null)
                {
                    HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: Cannot persist - instrument not available in NT");
                    return;
                }

                // Write ticks to NinjaTrader database via BarsUpdateRequest
                // NinjaTrader's tick database is written via subscription mechanism
                // We need to use the adapter's subscription to trigger persistence
                var adapter = Connector.Instance?.GetAdapter() as ZerodhaAdapter;
                if (adapter == null)
                {
                    HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: Cannot persist - adapter not available");
                    return;
                }

                // Subscribe to the instrument via the adapter - this enables NT database persistence
                // NinjaTrader persists ticks when a subscription is active
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Subscribe with empty callback - enables NT persistence
                        adapter.SubscribeMarketData(ntInstrument, (t, p, v, time, a5) => { });
                        HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: Subscribed to market data");
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: Subscription setup error: {ex.Message}");
                    }
                });

                // Use BarsRequest to trigger NT to write historical data
                // NT will call our BarsWorker which can serve the cached ICICI data
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Create a BarsRequest - NT will call BarsWorker to get data
                        // Using Tick type to match HasTickDataInNT8Async check (in Indian markets, tick ≈ second)
                        var barsRequest = new BarsRequest(ntInstrument, candles.Count);
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = candles.First().DateTime;
                        barsRequest.ToLocal = candles.Last().DateTime;
                        barsRequest.IsResetOnNewTradingDay = false;

                        HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: BarsRequest created - Type=Tick, From={barsRequest.FromLocal:HH:mm:ss}, To={barsRequest.ToLocal:HH:mm:ss}");

                        barsRequest.Request((barsResult, errorCode, errorMessage) =>
                        {
                            if (errorCode == ErrorCode.NoError)
                            {
                                // Now add our ICICI historical data to the bars
                                if (barsResult != null && barsResult.Bars != null)
                                {
                                    int beforeCount = barsResult.Bars.Count;
                                    foreach (var candle in candles)
                                    {
                                        barsResult.Bars.Add(
                                            (double)candle.Open,
                                            (double)candle.High,
                                            (double)candle.Low,
                                            (double)candle.Close,
                                            candle.DateTime,
                                            candle.Volume,
                                            double.MinValue,
                                            double.MinValue);
                                    }
                                    int afterCount = barsResult.Bars.Count;
                                    HistoricalTickLogger.Info($"[NT8-PERSIST] {zerodhaSymbol}: BarsRequest callback SUCCESS - added {candles.Count} bars (before={beforeCount}, after={afterCount})");
                                }
                                else
                                {
                                    HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: BarsRequest callback - barsResult or Bars is null");
                                }
                            }
                            else
                            {
                                HistoricalTickLogger.Warn($"[NT8-PERSIST] {zerodhaSymbol}: BarsRequest callback FAILED - ErrorCode={errorCode}, Message={errorMessage}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Error($"[NT8-PERSIST] {zerodhaSymbol}: Exception creating BarsRequest: {ex.Message}");
                    }
                });

                HistoricalTickLogger.Info($"[NT8-PERSIST] END {zerodhaSymbol}: Persistence initiated");
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[HistoricalTickDataService] PersistToNinjaTraderDatabaseAsync error: {ex.Message}");
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
            HistoricalTickLogger.Info("[HistoricalTickDataService] Cache cleared");
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

        public override void Dispose()
        {
            _iciciStatusSubscription?.Dispose();
            _requestQueueSubscription?.Dispose();
            _requestQueue?.Dispose();
            _serviceStatusSubject?.Dispose();
            _progressSubject?.Dispose();

            foreach (var subject in _strikeStatusSubjects.Values)
            {
                subject?.Dispose();
            }
            _strikeStatusSubjects.Clear();

            base.Dispose();

            HistoricalTickLogger.Info("[HistoricalTickDataService] Disposed");
        }

        #endregion
    }

    #region Models

    // (Internal) Parsed option symbol information
    internal class OptionSymbolInfo
    {
        public string Underlying { get; set; }
        public DateTime Expiry { get; set; }
        public int Strike { get; set; }
        public string OptionType { get; set; }
    }

    #endregion
}
