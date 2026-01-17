using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Core;
using ZerodhaDatafeedAdapter.Services.Historical.Providers;
using ZerodhaDatafeedAdapter.Services.Historical.Persistence;
using ZerodhaDatafeedAdapter.Services.Historical.Adapters;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Service for fetching historical tick data from Accelpix API.
    /// Implements IHistoricalTickDataSource for unified tick data access.
    ///
    /// Features:
    /// - No session/login required (API key only)
    /// - Higher throughput allowed (8-10 parallel requests vs ICICI's 4)
    /// - 3 prior working days + current day (vs ICICI's 1 prior day)
    /// - Uses same SQLite cache (IciciTickCacheDb) for storage
    /// </summary>
    public class AccelpixHistoricalTickDataService : BaseHistoricalTickDataService
    {
        private static readonly Lazy<AccelpixHistoricalTickDataService> _instance =
            new Lazy<AccelpixHistoricalTickDataService>(() => new AccelpixHistoricalTickDataService());

        public static AccelpixHistoricalTickDataService Instance => _instance.Value;

        #region Constants

        // Parallel fetch configuration - can be more aggressive than ICICI
        private const int PARALLEL_REQUESTS = 8;
        private const int RATE_LIMIT_DELAY_MS = 200;

        // Number of prior working days to fetch (excluding current day)
        private const int DEFAULT_DAYS_TO_FETCH = 3;

        #endregion

        // Accelpix-specific request queue (for batch downloads - uses pre-built symbols)
        private readonly ReplaySubject<AccelpixInstrumentRequest> _accelpixRequestQueue;
        private IDisposable _accelpixQueueSubscription;

        #region State

        private AccelpixApiClient _apiClient;
        private string _apiKey;
        private bool _isInitialized;
        private int _daysToFetch = DEFAULT_DAYS_TO_FETCH;
        private readonly object _initLock = new object();

        // New architecture dependencies
        private IHistoricalDataProvider _provider;
        private ITickDataPersistence _persistence;
        private INT8BarsRequestAdapter _nt8Adapter;

        #endregion

        #region Properties

        public override bool IsInitialized => _isInitialized;

        /// <summary>
        /// True when service is fully ready to process requests (initialized AND has valid API key)
        /// </summary>
        public override bool IsReady => _isInitialized && _apiClient != null && !string.IsNullOrEmpty(_apiKey);

        /// <summary>
        /// Number of prior working days to fetch (configurable via config.json)
        /// </summary>
        public int DaysToFetch => _daysToFetch;

        #endregion

        #region Constructor

        private AccelpixHistoricalTickDataService() : base(bufferSize: 200)
        {
            _accelpixRequestQueue = new ReplaySubject<AccelpixInstrumentRequest>(bufferSize: 500);

            HistoricalTickLogger.Info("[AccelpixHistoricalTickDataService] Singleton instance created");
        }

        #endregion

        #region IHistoricalTickDataSource Implementation

        /// <summary>
        /// Initialize the service - loads API key from config and sets up request queue processing.
        /// </summary>
        public override void Initialize()
        {
            if (_isInitialized)
            {
                HistoricalTickLogger.Info("[AccelpixHistoricalTickDataService] Already initialized, skipping");
                return;
            }

            lock (_initLock)
            {
                if (_isInitialized) return;

                HistoricalTickLogger.Info("[AccelpixHistoricalTickDataService] Initializing...");

                // Load API key from config
                if (!LoadCredentials())
                {
                    HistoricalTickLogger.Error("[AccelpixHistoricalTickDataService] Failed to load API credentials");
                    return;
                }

                // Create API client and initialize provider
                _apiClient = new AccelpixApiClient(_apiKey);
                _provider = ServiceFactory.GetAccelpixProvider(_apiClient);

                bool providerInitialized = _provider.InitializeAsync(_apiKey, null).GetAwaiter().GetResult();
                if (!providerInitialized)
                {
                    HistoricalTickLogger.Error("[AccelpixHistoricalTickDataService] Provider initialization failed");
                    return;
                }

                // Initialize persistence and NT8 adapter
                _persistence = ServiceFactory.GetTickPersistence();
                _nt8Adapter = ServiceFactory.GetNT8Adapter();

                // Subscribe to request queues for processing
                SubscribeToInstrumentQueue();
                SubscribeToAccelpixQueue();

                _isInitialized = true;
                HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Initialized - DaysToFetch={_daysToFetch}, ParallelRequests={PARALLEL_REQUESTS}");
            }
        }

        #region Queue Processing

        /// <summary>
        /// Queue batch download request using context (preferred method).
        /// Builds Accelpix symbols directly from underlying/expiry/strike context,
        /// avoiding symbol parsing. Similar to ICICI's QueueDownloadRequest.
        /// </summary>
        /// <param name="underlying">Underlying index (NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX)</param>
        /// <param name="expiry">Expiry date</param>
        /// <param name="projectedAtmStrike">Projected ATM strike for center-out propagation</param>
        /// <param name="strikes">List of all strikes to download</param>
        /// <param name="isMonthlyExpiry">Whether this is a monthly expiry</param>
        /// <param name="zerodhaSymbolMap">Optional mapping of (strike,optionType) to Zerodha trading symbol for cache keying</param>
        /// <param name="historicalDate">Date to fetch history for (defaults to prior working day)</param>
        public void QueueDownloadRequest(
            string underlying,
            DateTime expiry,
            int projectedAtmStrike,
            List<int> strikes,
            bool isMonthlyExpiry,
            Dictionary<(int strike, string optionType), string> zerodhaSymbolMap = null,
            DateTime? historicalDate = null)
        {
            if (string.IsNullOrEmpty(underlying) || strikes == null || strikes.Count == 0)
            {
                HistoricalTickLogger.Warn("[QueueDownloadRequest] Invalid request - missing underlying or strikes");
                return;
            }

            HistoricalTickLogger.Info($"[QueueDownloadRequest] Queueing batch: {underlying} {expiry:dd-MMM-yy} ATM={projectedAtmStrike} Strikes={strikes.Count} Monthly={isMonthlyExpiry}");

            // Sort strikes and order by distance from ATM (center-out propagation)
            var sortedStrikes = strikes.OrderBy(s => s).ToList();
            var orderedStrikes = sortedStrikes
                .OrderBy(s => Math.Abs(s - projectedAtmStrike))
                .ToList();

            // Queue each strike's CE and PE
            DateTime tradeDate = historicalDate ?? DateTime.Today;
            int queued = 0;

            foreach (var strike in orderedStrikes)
            {
                foreach (var optionType in new[] { "CE", "PE" })
                {
                    // Build Zerodha symbol for cache key
                    string zerodhaSymbol;
                    if (zerodhaSymbolMap != null && zerodhaSymbolMap.TryGetValue((strike, optionType), out var mappedSymbol))
                    {
                        zerodhaSymbol = mappedSymbol;
                    }
                    else
                    {
                        // Build using helper
                        zerodhaSymbol = Helpers.SymbolHelper.BuildOptionSymbol(underlying, expiry, strike, optionType, isMonthlyExpiry);
                    }

                    // Build Accelpix symbol using context (no parsing needed)
                    string accelpixSymbol = AccelpixSymbolMapper.BuildAccelpixSymbol(underlying, expiry, strike, optionType, isMonthlyExpiry);

                    if (string.IsNullOrEmpty(accelpixSymbol))
                    {
                        HistoricalTickLogger.Warn($"[QueueDownloadRequest] Could not build Accelpix symbol for {underlying} {strike} {optionType}");
                        continue;
                    }

                    // Create request with both symbols
                    var request = new AccelpixInstrumentRequest
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        AccelpixSymbol = accelpixSymbol,
                        TradeDate = tradeDate,
                        QueuedAt = DateTime.Now
                    };

                    // Get or create status subject using Zerodha symbol as key
                    var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                        _ => new BehaviorSubject<InstrumentTickDataStatus>(
                            new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

                    // Check if already ready/downloading
                    var currentStatus = statusSubject.Value;
                    if (currentStatus.State == TickDataState.Ready || currentStatus.State == TickDataState.Downloading)
                    {
                        continue; // Skip if already processed/processing
                    }

                    // Update status to queued
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Queued,
                        TradeDate = tradeDate
                    });

                    // Queue the request using the enhanced request type
                    _accelpixRequestQueue.OnNext(request);
                    queued++;
                }
            }

            HistoricalTickLogger.Info($"[QueueDownloadRequest] Queued {queued} symbols for {underlying} {expiry:dd-MMM-yy}");
        }

        #endregion

        #region Private Methods

        private bool LoadCredentials()
        {
            try
            {
                string configPath = Classes.Constants.GetFolderPath(Classes.Constants.ConfigFileName);

                if (!File.Exists(configPath))
                {
                    HistoricalTickLogger.Error($"[AccelpixHistoricalTickDataService] Config file not found: {configPath}");
                    return false;
                }

                string json = File.ReadAllText(configPath);
                var config = JObject.Parse(json);
                var accelpixConfig = config["Accelpix"] as JObject;

                if (accelpixConfig != null)
                {
                    _apiKey = accelpixConfig["ApiKey"]?.ToString();

                    // Load days to fetch setting
                    var daysToFetchValue = accelpixConfig["DaysToFetch"];
                    if (daysToFetchValue != null)
                    {
                        _daysToFetch = daysToFetchValue.Value<int>();
                        if (_daysToFetch < 1) _daysToFetch = 1;
                        if (_daysToFetch > 7) _daysToFetch = 7; // Cap at 7 days
                    }

                    HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Loaded API credentials (key={_apiKey?.Substring(0, Math.Min(8, _apiKey?.Length ?? 0))}...)");
                    return !string.IsNullOrEmpty(_apiKey);
                }

                HistoricalTickLogger.Error("[AccelpixHistoricalTickDataService] Accelpix config section not found");
                return false;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[AccelpixHistoricalTickDataService] Error loading credentials: {ex.Message}", ex);
                return false;
            }
        }

        protected override void SubscribeToInstrumentQueue()
        {
            if (_instrumentQueueSubscription != null)
            {
                HistoricalTickLogger.Debug("[AccelpixHistoricalTickDataService] Already subscribed to instrument queue");
                return;
            }

            HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Subscribing to instrument queue - processing {PARALLEL_REQUESTS} at a time with backpressure");

            // Use Concat to ensure sequential batch processing (true backpressure)
            // Each batch waits for previous batch to complete before starting
            _instrumentQueueSubscription = _instrumentRequestQueue
                .Buffer(PARALLEL_REQUESTS)
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

        /// <summary>
        /// Subscribe to the Accelpix-specific request queue (for batch downloads with pre-built symbols).
        /// </summary>
        private void SubscribeToAccelpixQueue()
        {
            if (_accelpixQueueSubscription != null)
            {
                HistoricalTickLogger.Debug("[AccelpixHistoricalTickDataService] Already subscribed to Accelpix queue");
                return;
            }

            HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Subscribing to Accelpix queue - processing {PARALLEL_REQUESTS} at a time with backpressure");

            _accelpixQueueSubscription = _accelpixRequestQueue
                .Buffer(PARALLEL_REQUESTS)
                .Where(batch => batch.Count > 0)
                .Select(batch => Observable.FromAsync(async () =>
                {
                    try
                    {
                        HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] Processing batch of {batch.Count} instruments");

                        // Process all instruments in batch concurrently
                        var tasks = batch.Select(req => ProcessAccelpixRequestAsync(req)).ToList();
                        await Task.WhenAll(tasks);

                        await Task.Delay(RATE_LIMIT_DELAY_MS);

                        return System.Reactive.Unit.Default;
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Error($"[ACCELPIX-QUEUE] Batch processing error: {ex.Message}");
                        return System.Reactive.Unit.Default;
                    }
                }))
                .Concat() // Concat ensures sequential execution - only 1 batch in flight at a time
                .Subscribe(
                    _ => { /* Batch completed */ },
                    ex => HistoricalTickLogger.Error($"[ACCELPIX-QUEUE] Queue error: {ex.Message}"),
                    () => HistoricalTickLogger.Info("[ACCELPIX-QUEUE] Queue completed")
                );
        }

        /// <summary>
        /// Process an Accelpix request with pre-built symbols (no parsing needed).
        /// Uses new architecture: provider, persistence, and NT8 adapter.
        /// </summary>
        private async Task ProcessAccelpixRequestAsync(AccelpixInstrumentRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ZerodhaSymbol) || string.IsNullOrEmpty(request.AccelpixSymbol))
                return;

            string zerodhaSymbol = request.ZerodhaSymbol;
            string accelpixSymbol = request.AccelpixSymbol;

            var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

            try
            {
                // Check if request is stale
                var age = DateTime.Now - request.QueuedAt;
                if (age.TotalMinutes > 10)
                {
                    HistoricalTickLogger.Warn($"[NEW-ARCH] Skipping stale request for {zerodhaSymbol} (age={age.TotalMinutes:F1}min)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Failed,
                        ErrorMessage = "Request expired"
                    });
                    return;
                }

                // Determine days to fetch
                var daysToFetch = GetDaysToFetch();
                HistoricalTickLogger.Info($"[NEW-ARCH] {zerodhaSymbol} -> {accelpixSymbol}: Fetching {daysToFetch.Count} days");

                // Check cache first
                bool allCached = true;
                foreach (var day in daysToFetch)
                {
                    if (!await _persistence.HasCachedDataAsync(zerodhaSymbol, day))
                    {
                        allCached = false;
                        break;
                    }
                }

                if (allCached)
                {
                    var cached = await _persistence.GetCachedTicksAsync(zerodhaSymbol, daysToFetch.First(), daysToFetch.Last());
                    HistoricalTickLogger.Info($"[NEW-ARCH] CACHE HIT: {zerodhaSymbol} has {cached.Count} ticks from persistence");

                    await _nt8Adapter.TriggerBarsRequestAsync(zerodhaSymbol, cached);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = cached.Count
                    });
                    return;
                }

                // Update status to downloading
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Downloading,
                    TradeDate = daysToFetch.LastOrDefault()
                });

                // Fetch from provider
                var allTicks = new List<HistoricalCandle>();
                foreach (var tradeDate in daysToFetch)
                {
                    HistoricalTickLogger.Info($"[NEW-ARCH] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Fetching via provider...");

                    var ticks = await _provider.FetchTickDataAsync(accelpixSymbol, tradeDate, tradeDate);
                    if (ticks != null && ticks.Count > 0)
                    {
                        var candles = ticks.Select(t => new HistoricalCandle
                        {
                            DateTime = t.DateTime,
                            Open = t.Open,
                            High = t.High,
                            Low = t.Low,
                            Close = t.Close,
                            Volume = t.Volume
                        }).Where(c => c.Volume > 0).ToList();

                        if (candles.Count > 0)
                        {
                            await _persistence.CacheTicksAsync(zerodhaSymbol, tradeDate, candles);
                            allTicks.AddRange(candles);
                            HistoricalTickLogger.Info($"[NEW-ARCH] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Cached {candles.Count} ticks");
                        }
                    }

                    if (daysToFetch.Count > 1)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                if (allTicks.Count > 0)
                {
                    await _nt8Adapter.TriggerBarsRequestAsync(zerodhaSymbol, allTicks);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = allTicks.Count
                    });

                    HistoricalTickLogger.Info($"[NEW-ARCH] {zerodhaSymbol}: SUCCESS - {allTicks.Count} ticks processed");
                }
                else
                {
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.NoData,
                        TradeDate = daysToFetch.LastOrDefault(),
                        ErrorMessage = "No data from provider"
                    });
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[NEW-ARCH] Error processing {zerodhaSymbol}: {ex.Message}", ex);
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Process a single instrument tick data request.
        /// Uses new architecture: provider, persistence, and NT8 adapter.
        /// </summary>
        private async Task ProcessInstrumentRequestAsync(InstrumentTickDataRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ZerodhaSymbol))
                return;

            string zerodhaSymbol = request.ZerodhaSymbol;

            var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

            try
            {
                // Check if request is stale
                var age = DateTime.Now - request.QueuedAt;
                if (age.TotalMinutes > 10)
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] Skipping stale request for {zerodhaSymbol} (age={age.TotalMinutes:F1}min)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Failed,
                        ErrorMessage = "Request expired"
                    });
                    return;
                }

                // Map Zerodha symbol to Accelpix format
                string accelpixSymbol = AccelpixSymbolMapper.MapZerodhaToAccelpix(zerodhaSymbol);
                if (string.IsNullOrEmpty(accelpixSymbol))
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] Cannot map symbol: {zerodhaSymbol}");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Failed,
                        ErrorMessage = "Cannot map symbol to Accelpix format"
                    });
                    return;
                }

                // Determine days to fetch
                var daysToFetch = GetDaysToFetch();
                HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} -> {accelpixSymbol}: Fetching {daysToFetch.Count} days");

                // Check cache first
                bool allCached = true;
                foreach (var day in daysToFetch)
                {
                    if (!await _persistence.HasCachedDataAsync(zerodhaSymbol, day))
                    {
                        allCached = false;
                        break;
                    }
                }

                if (allCached)
                {
                    var cached = await _persistence.GetCachedTicksAsync(zerodhaSymbol, daysToFetch.First(), daysToFetch.Last());
                    HistoricalTickLogger.Info($"[INST-QUEUE] CACHE HIT: {zerodhaSymbol} has {cached.Count} ticks");

                    await _nt8Adapter.TriggerBarsRequestAsync(zerodhaSymbol, cached);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = cached.Count
                    });
                    return;
                }

                // Update status to downloading
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Downloading,
                    TradeDate = daysToFetch.LastOrDefault()
                });

                // Fetch from provider
                var allTicks = new List<HistoricalCandle>();
                foreach (var tradeDate in daysToFetch)
                {
                    HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Fetching via provider...");

                    var ticks = await _provider.FetchTickDataAsync(accelpixSymbol, tradeDate, tradeDate);
                    if (ticks != null && ticks.Count > 0)
                    {
                        var candles = ticks.Select(t => new HistoricalCandle
                        {
                            DateTime = t.DateTime,
                            Open = t.Open,
                            High = t.High,
                            Low = t.Low,
                            Close = t.Close,
                            Volume = t.Volume
                        }).Where(c => c.Volume > 0).ToList();

                        if (candles.Count > 0)
                        {
                            await _persistence.CacheTicksAsync(zerodhaSymbol, tradeDate, candles);
                            allTicks.AddRange(candles);
                            HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Cached {candles.Count} ticks");
                        }
                    }

                    if (daysToFetch.Count > 1)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                if (allTicks.Count > 0)
                {
                    await _nt8Adapter.TriggerBarsRequestAsync(zerodhaSymbol, allTicks);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = allTicks.Count
                    });

                    HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol}: SUCCESS - {allTicks.Count} ticks processed");
                }
                else
                {
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.NoData,
                        TradeDate = daysToFetch.LastOrDefault(),
                        ErrorMessage = "No data from provider"
                    });
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[INST-QUEUE] Error processing {zerodhaSymbol}: {ex.Message}", ex);
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Failed,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Determine which days to fetch based on current IST time.
        /// Accelpix fetches up to _daysToFetch prior working days + current day.
        /// </summary>
        private List<DateTime> GetDaysToFetch()
        {
            var result = new List<DateTime>();
            var istNow = GetCurrentIstTime();

            // Market timing
            var marketOpen = new TimeSpan(9, 15, 0);
            var timeOfDay = istNow.TimeOfDay;

            // Get prior working days
            var currentDate = istNow.Date;
            int daysAdded = 0;

            // Go back and collect working days
            for (int i = 1; i <= 10 && daysAdded < _daysToFetch; i++)
            {
                var checkDate = currentDate.AddDays(-i);
                if (HolidayCalendarService.Instance.IsTradingDay(checkDate))
                {
                    result.Add(checkDate);
                    daysAdded++;
                }
            }

            // Reverse so oldest is first
            result.Reverse();

            // Add current day if market has opened
            if (timeOfDay >= marketOpen && HolidayCalendarService.Instance.IsTradingDay(currentDate))
            {
                result.Add(currentDate);
            }

            HistoricalTickLogger.Debug($"[GetDaysToFetch] IST={istNow:HH:mm}, Days to fetch: {string.Join(", ", result.Select(d => d.ToString("yyyy-MM-dd")))}");
            return result;
        }

        /// <summary>
        /// Get current time in IST timezone.
        /// </summary>
        private DateTime GetCurrentIstTime()
        {
            TimeZoneInfo istZone = TimeZoneInfo.FindSystemTimeZoneById(Classes.Constants.IndianTimeZoneId);
            return TimeZoneInfo.ConvertTime(DateTime.Now, istZone);
        }

        #endregion

        #region IDisposable

        public override void Dispose()
        {
            _accelpixQueueSubscription?.Dispose();
            _accelpixRequestQueue?.Dispose();
            _apiClient?.Dispose();
            base.Dispose();

            HistoricalTickLogger.Info("[AccelpixHistoricalTickDataService] Disposed");
        }

        #endregion
    }

    /// <summary>
    /// Request model for Accelpix tick data downloads with pre-built symbols.
    /// Used by QueueDownloadRequest for batch downloads where context is available.
    /// </summary>
    public class AccelpixInstrumentRequest
    {
        /// <summary>
        /// Zerodha trading symbol (used as cache key)
        /// </summary>
        public string ZerodhaSymbol { get; set; }

        /// <summary>
        /// Pre-built Accelpix symbol (avoids parsing)
        /// </summary>
        public string AccelpixSymbol { get; set; }

        /// <summary>
        /// Trade date to fetch
        /// </summary>
        public DateTime TradeDate { get; set; }

        /// <summary>
        /// When the request was queued
        /// </summary>
        public DateTime QueuedAt { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return $"{ZerodhaSymbol} -> {AccelpixSymbol} for {TradeDate:yyyy-MM-dd}";
        }
    }
    #endregion
}
