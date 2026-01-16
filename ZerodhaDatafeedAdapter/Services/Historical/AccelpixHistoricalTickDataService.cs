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

                // Create API client
                _apiClient = new AccelpixApiClient(_apiKey);

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
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, Classes.Constants.BaseDataFolder, Classes.Constants.ConfigFileName);

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

            HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Subscribing to instrument queue - processing {PARALLEL_REQUESTS} at a time");

            // Use Buffer with count=PARALLEL_REQUESTS to process instruments in parallel batches
            _instrumentQueueSubscription = _instrumentRequestQueue
                .Buffer(PARALLEL_REQUESTS)
                .Where(batch => batch.Count > 0)
                .Subscribe(
                    batch =>
                    {
                        // Process batch on background thread
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                HistoricalTickLogger.Info($"[INST-QUEUE] Processing batch of {batch.Count} instruments");
                                var tasks = batch.Select(req => ProcessInstrumentRequestAsync(req)).ToList();
                                await Task.WhenAll(tasks);

                                // Rate limit between batches
                                await Task.Delay(RATE_LIMIT_DELAY_MS);
                            }
                            catch (Exception ex)
                            {
                                HistoricalTickLogger.Error($"[INST-QUEUE] Batch processing error: {ex.Message}");
                            }
                        });
                    },
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

            HistoricalTickLogger.Info($"[AccelpixHistoricalTickDataService] Subscribing to Accelpix queue - processing {PARALLEL_REQUESTS} at a time");

            _accelpixQueueSubscription = _accelpixRequestQueue
                .Buffer(PARALLEL_REQUESTS)
                .Where(batch => batch.Count > 0)
                .Subscribe(
                    batch =>
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] Processing batch of {batch.Count} instruments");
                                var tasks = batch.Select(req => ProcessAccelpixRequestAsync(req)).ToList();
                                await Task.WhenAll(tasks);

                                await Task.Delay(RATE_LIMIT_DELAY_MS);
                            }
                            catch (Exception ex)
                            {
                                HistoricalTickLogger.Error($"[ACCELPIX-QUEUE] Batch processing error: {ex.Message}");
                            }
                        });
                    },
                    ex => HistoricalTickLogger.Error($"[ACCELPIX-QUEUE] Queue error: {ex.Message}"),
                    () => HistoricalTickLogger.Info("[ACCELPIX-QUEUE] Queue completed")
                );
        }

        /// <summary>
        /// Process an Accelpix request with pre-built symbols (no parsing needed).
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
                    HistoricalTickLogger.Warn($"[ACCELPIX-QUEUE] Skipping stale request for {zerodhaSymbol} (age={age.TotalMinutes:F1}min)");
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
                HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] {zerodhaSymbol} -> {accelpixSymbol}: Fetching {daysToFetch.Count} days");

                // Check if all days are cached in SQLite
                bool allDaysCached = daysToFetch.All(d => IciciTickCacheDb.Instance.HasCachedData(zerodhaSymbol, d));

                if (allDaysCached)
                {
                    int totalTicks = 0;
                    foreach (var date in daysToFetch)
                    {
                        var cached = IciciTickCacheDb.Instance.GetCachedTicks(zerodhaSymbol, date);
                        if (cached != null) totalTicks += cached.Count;
                    }

                    HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] SQLITE CACHE HIT: {zerodhaSymbol} has {totalTicks} ticks across {daysToFetch.Count} day(s)");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = totalTicks
                    });
                    return;
                }

                // Check if NT8 already has tick data persisted (from previous session)
                // This avoids re-downloading data that NT8 already has
                // We check EACH day individually to ensure complete coverage
                if (daysToFetch.Count > 0)
                {
                    bool nt8HasAllDays = await CheckNT8HasTickDataForDaysAsync(zerodhaSymbol, daysToFetch);
                    if (nt8HasAllDays)
                    {
                        HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] NT8 DB HIT: {zerodhaSymbol} - skipping download (all {daysToFetch.Count} days already persisted)");
                        statusSubject.OnNext(new InstrumentTickDataStatus
                        {
                            ZerodhaSymbol = zerodhaSymbol,
                            State = TickDataState.Ready,
                            TradeDate = daysToFetch.Last(),
                            TickCount = -1 // Unknown count, but data exists
                        });
                        return;
                    }
                }

                // Update status
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Downloading,
                    TradeDate = daysToFetch.LastOrDefault()
                });

                int totalDownloaded = 0;
                int totalFiltered = 0;
                var allCandles = new List<HistoricalCandle>();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Download each day, store to SQLite cache, then trigger NT8 BarsRequest
                foreach (var tradeDate in daysToFetch)
                {
                    HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Fetching from Accelpix as {accelpixSymbol}...");

                    var accelpixTicks = await _apiClient.GetTickDataAsync(accelpixSymbol, tradeDate);
                    totalDownloaded += accelpixTicks?.Count ?? 0;

                    if (accelpixTicks != null && accelpixTicks.Count > 0)
                    {
                        // Convert with delta volume calculation (cumulative qty -> per-tick volume)
                        var candles = AccelpixApiClient.ConvertToHistoricalCandles(accelpixTicks);
                        var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                        int removed = candles.Count - filteredCandles.Count;

                        HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Downloaded {accelpixTicks.Count} ticks, {filteredCandles.Count} with volume (removed {removed} zero-volume)");

                        if (filteredCandles.Count > 0)
                        {
                            // Store to SQLite cache per day - BarsWorker will read from here
                            TickCacheDb.Instance.CacheTicks(zerodhaSymbol, tradeDate, filteredCandles);
                            HistoricalTickLogger.Info($"[ACCELPIX-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Cached {filteredCandles.Count} ticks to SQLite");

                            allCandles.AddRange(filteredCandles);
                            totalFiltered += filteredCandles.Count;
                        }
                    }
                    else
                    {
                        HistoricalTickLogger.Warn($"[ACCELPIX-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: No data from Accelpix API");
                    }

                    if (daysToFetch.Count > 1)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                sw.Stop();

                if (totalFiltered > 0 && allCandles.Count > 0)
                {
                    HistoricalTickLogger.LogDownloadComplete("ACCELPIX", zerodhaSymbol, daysToFetch.Last(), totalFiltered, sw.ElapsedMilliseconds);

                    // Trigger NT8 BarsRequest - it will call BarsWorker which reads from SQLite cache
                    await TriggerNT8BarsRequestAsync(zerodhaSymbol, allCandles);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = totalFiltered
                    });
                }
                else
                {
                    HistoricalTickLogger.Warn($"[ACCELPIX-QUEUE] {zerodhaSymbol}: No data after filtering (downloaded {totalDownloaded})");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.NoData,
                        TradeDate = daysToFetch.LastOrDefault(),
                        ErrorMessage = totalDownloaded > 0 ? "All ticks had zero volume" : "No data from Accelpix API"
                    });
                }
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[ACCELPIX-QUEUE] Error processing {zerodhaSymbol}: {ex.Message}", ex);
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
        /// Downloads from Accelpix and persists directly to NT8 database (no local cache).
        /// </summary>
        private async Task ProcessInstrumentRequestAsync(InstrumentTickDataRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ZerodhaSymbol))
                return;

            string zerodhaSymbol = request.ZerodhaSymbol;

            // Get or create status subject
            var statusSubject = _instrumentStatusSubjects.GetOrAdd(zerodhaSymbol,
                _ => new BehaviorSubject<InstrumentTickDataStatus>(
                    new InstrumentTickDataStatus { ZerodhaSymbol = zerodhaSymbol, State = TickDataState.Pending }));

            try
            {
                // Check if request is stale (older than 10 minutes)
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

                // Determine which days to fetch
                var daysToFetch = GetDaysToFetch();
                HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} -> {accelpixSymbol}: Fetching {daysToFetch.Count} days");

                // Check if NT8 already has tick data persisted (from previous session)
                // We check EACH day individually to ensure complete coverage
                if (daysToFetch.Count > 0)
                {
                    bool nt8HasAllDays = await CheckNT8HasTickDataForDaysAsync(zerodhaSymbol, daysToFetch);
                    if (nt8HasAllDays)
                    {
                        HistoricalTickLogger.Info($"[INST-QUEUE] NT8 DB HIT: {zerodhaSymbol} - skipping download (all {daysToFetch.Count} days already persisted)");
                        statusSubject.OnNext(new InstrumentTickDataStatus
                        {
                            ZerodhaSymbol = zerodhaSymbol,
                            State = TickDataState.Ready,
                            TradeDate = daysToFetch.Last(),
                            TickCount = -1
                        });
                        return;
                    }
                }

                // Update status to downloading
                statusSubject.OnNext(new InstrumentTickDataStatus
                {
                    ZerodhaSymbol = zerodhaSymbol,
                    State = TickDataState.Downloading,
                    TradeDate = daysToFetch.LastOrDefault()
                });

                int totalDownloaded = 0;
                int totalFiltered = 0;
                var allCandles = new List<HistoricalCandle>();
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Download each day, store to SQLite cache, then trigger NT8 BarsRequest
                foreach (var tradeDate in daysToFetch)
                {
                    HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Fetching from Accelpix...");

                    // Fetch from Accelpix API
                    var accelpixTicks = await _apiClient.GetTickDataAsync(accelpixSymbol, tradeDate);
                    totalDownloaded += accelpixTicks?.Count ?? 0;

                    if (accelpixTicks != null && accelpixTicks.Count > 0)
                    {
                        // Convert with delta volume calculation (cumulative qty -> per-tick volume)
                        var candles = AccelpixApiClient.ConvertToHistoricalCandles(accelpixTicks);
                        var filteredCandles = candles.Where(c => c.Volume > 0).ToList();
                        int removed = candles.Count - filteredCandles.Count;

                        HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Downloaded {accelpixTicks.Count} ticks, {filteredCandles.Count} with volume (removed {removed} zero-volume)");

                        if (filteredCandles.Count > 0)
                        {
                            // Store to SQLite cache per day - BarsWorker will read from here
                            TickCacheDb.Instance.CacheTicks(zerodhaSymbol, tradeDate, filteredCandles);
                            HistoricalTickLogger.Info($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: Cached {filteredCandles.Count} ticks to SQLite");

                            allCandles.AddRange(filteredCandles);
                            totalFiltered += filteredCandles.Count;
                        }
                    }
                    else
                    {
                        HistoricalTickLogger.Warn($"[INST-QUEUE] {zerodhaSymbol} {tradeDate:yyyy-MM-dd}: No data from Accelpix API");
                    }

                    // Small delay between requests
                    if (daysToFetch.Count > 1)
                    {
                        await Task.Delay(RATE_LIMIT_DELAY_MS);
                    }
                }

                sw.Stop();

                // Final status update
                if (totalFiltered > 0 && allCandles.Count > 0)
                {
                    HistoricalTickLogger.LogDownloadComplete("ACCELPIX", zerodhaSymbol, daysToFetch.Last(), totalFiltered, sw.ElapsedMilliseconds);

                    // Trigger NT8 BarsRequest - it will call BarsWorker which reads from SQLite cache
                    await TriggerNT8BarsRequestAsync(zerodhaSymbol, allCandles);

                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.Ready,
                        TradeDate = daysToFetch.Last(),
                        TickCount = totalFiltered
                    });
                }
                else
                {
                    HistoricalTickLogger.Warn($"[INST-QUEUE] {zerodhaSymbol}: No data after filtering (downloaded {totalDownloaded})");
                    statusSubject.OnNext(new InstrumentTickDataStatus
                    {
                        ZerodhaSymbol = zerodhaSymbol,
                        State = TickDataState.NoData,
                        TradeDate = daysToFetch.LastOrDefault(),
                        ErrorMessage = totalDownloaded > 0 ? "All ticks had zero volume" : "No data from Accelpix API"
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

        /// <summary>
        /// Check if NT8 already has tick data for the most recent day.
        /// If the most recent day has >500 ticks, consider the instrument up-to-date.
        /// This is a quick check to avoid unnecessary API downloads.
        /// </summary>
        private async Task<bool> CheckNT8HasTickDataForDaysAsync(string zerodhaSymbol, List<DateTime> daysToCheck)
        {
            if (daysToCheck == null || daysToCheck.Count == 0)
                return false;

            try
            {
                // Only check the most recent day - if it has data, skip backfill
                var mostRecentDay = daysToCheck.Last();
                var tcs = new TaskCompletionSource<int>();

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                        if (ntInstrument == null)
                        {
                            tcs.TrySetResult(0);
                            return;
                        }

                        DateTime fromDate = mostRecentDay.Date.AddHours(9).AddMinutes(15);  // 09:15 IST
                        DateTime toDate = mostRecentDay.Date.AddHours(15).AddMinutes(30);   // 15:30 IST

                        // Create a BarsRequest to probe NT8's database
                        var barsRequest = new BarsRequest(ntInstrument, 500);
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = fromDate.ToLocalTime();
                        barsRequest.ToLocal = toDate.ToLocalTime();

                        barsRequest.Request((barsResult, errorCode, errorMessage) =>
                        {
                            if (errorCode == ErrorCode.NoError && barsResult?.Bars != null)
                            {
                                tcs.TrySetResult(barsResult.Bars.Count);
                            }
                            else
                            {
                                tcs.TrySetResult(0);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Debug($"[NT8-CHECK] {zerodhaSymbol}: Exception: {ex.Message}");
                        tcs.TrySetResult(0);
                    }
                });

                // Wait for the BarsRequest callback with timeout
                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                int tickCount = 0;
                if (completedTask == tcs.Task)
                {
                    tickCount = tcs.Task.Result;
                }

                // If most recent day has >500 ticks, consider up-to-date
                bool hasData = tickCount >= 500;
                if (hasData)
                {
                    HistoricalTickLogger.Info($"[NT8-CHECK] {zerodhaSymbol}: NT8 has {tickCount} ticks for {mostRecentDay:MM-dd} - skipping download");
                }

                return hasData;
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Debug($"[NT8-CHECK] {zerodhaSymbol}: Error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region NT8 BarsRequest Trigger

        /// <summary>
        /// Triggers NT8 BarsRequest to read cached tick data via BarsWorker.
        /// Flow: BarsRequest -> NT8 calls BarsWorker -> BarsWorker reads from SQLite cache -> NT8 persists to tick db
        /// </summary>
        private async Task TriggerNT8BarsRequestAsync(string zerodhaSymbol, List<HistoricalCandle> candles)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol) || candles == null || candles.Count == 0)
                return;

            try
            {
                var firstCandle = candles.First();
                var lastCandle = candles.Last();
                HistoricalTickLogger.Info($"[NT8-TRIGGER] START {zerodhaSymbol}: {candles.Count} ticks cached, triggering BarsRequest for range {firstCandle.DateTime.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {lastCandle.DateTime.ToLocalTime():HH:mm:ss}");

                // Get or create the NT instrument on the UI thread
                Instrument ntInstrument = null;
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                    if (ntInstrument == null)
                    {
                        HistoricalTickLogger.Info($"[NT8-TRIGGER] {zerodhaSymbol}: Instrument not found, attempting to create...");
                        var mapping = InstrumentManager.Instance.GetMappingByNtSymbol(zerodhaSymbol);
                        if (mapping != null)
                        {
                            string ntName;
                            NinjaTraderHelper.CreateNTInstrumentFromMapping(mapping, out ntName);
                            ntInstrument = Instrument.GetInstrument(ntName);
                            HistoricalTickLogger.Info($"[NT8-TRIGGER] {zerodhaSymbol}: Created instrument, ntName={ntName}");
                        }
                        else
                        {
                            HistoricalTickLogger.Warn($"[NT8-TRIGGER] {zerodhaSymbol}: No mapping found in InstrumentManager");
                        }
                    }
                });

                if (ntInstrument == null)
                {
                    HistoricalTickLogger.Warn($"[NT8-TRIGGER] {zerodhaSymbol}: Cannot trigger - instrument not available in NT");
                    return;
                }

                // Trigger BarsRequest - NT8 will call our BarsWorker which reads from SQLite cache
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Create a BarsRequest for tick data
                        // NT8 will call BarsWorker -> GetTickDataFromIciciCache -> serves from SQLite -> NT8 persists
                        var barsRequest = new BarsRequest(ntInstrument, candles.Count);
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = firstCandle.DateTime.ToLocalTime();
                        barsRequest.ToLocal = lastCandle.DateTime.ToLocalTime();
                        barsRequest.IsResetOnNewTradingDay = false;

                        HistoricalTickLogger.Info($"[NT8-TRIGGER] {zerodhaSymbol}: BarsRequest created - Type=Tick, From={barsRequest.FromLocal:HH:mm:ss}, To={barsRequest.ToLocal:HH:mm:ss}");

                        barsRequest.Request((barsResult, errorCode, errorMessage) =>
                        {
                            if (errorCode == ErrorCode.NoError)
                            {
                                int barsCount = barsResult?.Bars?.Count ?? 0;
                                HistoricalTickLogger.Info($"[NT8-TRIGGER] {zerodhaSymbol}: BarsRequest callback SUCCESS - BarsWorker served {barsCount} bars from cache");

                                // Delete SQLite cache after NT8 has persisted the data
                                if (barsCount > 0)
                                {
                                    Task.Run(() =>
                                    {
                                        int deleted = TickCacheDb.Instance.DeleteCacheForSymbol(zerodhaSymbol);
                                        HistoricalTickLogger.Info($"[NT8-TRIGGER] {zerodhaSymbol}: Cleaned up SQLite cache ({deleted} ticks removed)");
                                    });
                                }
                            }
                            else
                            {
                                HistoricalTickLogger.Warn($"[NT8-TRIGGER] {zerodhaSymbol}: BarsRequest callback FAILED - ErrorCode={errorCode}, Message={errorMessage}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Error($"[NT8-TRIGGER] {zerodhaSymbol}: Exception creating BarsRequest: {ex.Message}");
                    }
                });

                HistoricalTickLogger.Info($"[NT8-TRIGGER] END {zerodhaSymbol}: BarsRequest triggered");
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[AccelpixHistoricalTickDataService] TriggerNT8BarsRequestAsync error: {ex.Message}");
            }
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
