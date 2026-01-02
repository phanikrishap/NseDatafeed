using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.SyntheticInstruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Manages historical data backfill for subscribed instruments.
    /// Handles batched historical data fetching and caching for CE/PE options.
    /// Extracted from SubscriptionManager for better separation of concerns.
    /// </summary>
    public class InstrumentBackfillService
    {
        #region Singleton

        private static InstrumentBackfillService _instance;
        public static InstrumentBackfillService Instance => _instance ?? (_instance = new InstrumentBackfillService());

        #endregion

        #region Configuration

        // Batching configuration for historical data requests
        // Zerodha allows ~3 requests/second for historical API, so 10 parallel with 4s delay = safe throughput
        private const int HISTORICAL_BATCH_SIZE = 10;
        private const int HISTORICAL_BATCH_DELAY_MS = 4000; // 4 seconds between batches

        #endregion

        #region State

        private readonly ConcurrentQueue<BackfillItem> _backfillQueue = new ConcurrentQueue<BackfillItem>();
        private bool _isProcessing = false;

        // Track processed instruments for downstream processing
        private readonly ConcurrentBag<(string ntSymbol, Instrument ntInstrument)> _processedInstruments = new ConcurrentBag<(string, Instrument)>();
        private readonly ConcurrentBag<string> _processedStraddleSymbols = new ConcurrentBag<string>();

        #endregion

        #region Events

        /// <summary>
        /// Fired when an instrument's historical data status changes (symbol, status like "Cached (123)" or "No Data")
        /// </summary>
        public event Action<string, string> BackfillStatusChanged;

        /// <summary>
        /// Fired when an instrument's price is updated from historical data
        /// </summary>
        public event Action<string, double> PriceUpdated;

        /// <summary>
        /// Fired when an instrument is ready for streaming (after successful backfill)
        /// </summary>
        public event Action<BackfillItem> InstrumentReadyForStreaming;

        /// <summary>
        /// Fired when all backfill processing is complete
        /// </summary>
        public event Action BackfillComplete;

        #endregion

        #region Properties

        /// <summary>
        /// Whether backfill processing is currently running
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// Count of items in backfill queue
        /// </summary>
        public int QueueCount => _backfillQueue.Count;

        /// <summary>
        /// Get list of processed instruments (for BarsRequest after backfill)
        /// </summary>
        public IEnumerable<(string ntSymbol, Instrument ntInstrument)> ProcessedInstruments => _processedInstruments;

        /// <summary>
        /// Get list of processed straddle symbols
        /// </summary>
        public IEnumerable<string> ProcessedStraddleSymbols => _processedStraddleSymbols.Distinct();

        #endregion

        #region Constructor

        private InstrumentBackfillService()
        {
            Logger.Info("[InstrumentBackfillService] Constructor: Initializing singleton instance");
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Queue an instrument for historical data backfill
        /// </summary>
        public void QueueBackfill(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)
        {
            var item = new BackfillItem
            {
                MappedInstrument = mappedInst,
                NTSymbol = ntSymbol,
                NTInstrument = ntInstrument
            };

            _backfillQueue.Enqueue(item);
            Logger.Debug($"[InstrumentBackfillService] Queued backfill for {ntSymbol}");

            // Start processor if not running
            if (!_isProcessing)
            {
                Logger.Info("[InstrumentBackfillService] Starting backfill processor...");
                _ = Task.Run(ProcessBackfillQueue);
            }
        }

        /// <summary>
        /// Queue multiple instruments for backfill
        /// </summary>
        public void QueueBackfillBatch(IEnumerable<(MappedInstrument mappedInst, string ntSymbol, Instrument ntInstrument)> items)
        {
            foreach (var (mappedInst, ntSymbol, ntInstrument) in items)
            {
                QueueBackfill(mappedInst, ntSymbol, ntInstrument);
            }
        }

        /// <summary>
        /// Clear all processed instruments (for reset)
        /// </summary>
        public void ClearProcessedInstruments()
        {
            while (_processedInstruments.TryTake(out _)) { }
            while (_processedStraddleSymbols.TryTake(out _)) { }
            Logger.Info("[InstrumentBackfillService] Cleared processed instruments");
        }

        #endregion

        #region Queue Processing

        /// <summary>
        /// Processes the backfill queue in batches to avoid overloading Zerodha API
        /// </summary>
        private async Task ProcessBackfillQueue()
        {
            Logger.Info("[InstrumentBackfillService] ProcessBackfillQueue(): Started batch processor");
            _isProcessing = true;

            int batchNumber = 0;
            int totalProcessed = 0;

            while (!_backfillQueue.IsEmpty)
            {
                // Collect a batch of instruments
                var batch = new List<BackfillItem>();
                while (batch.Count < HISTORICAL_BATCH_SIZE && _backfillQueue.TryDequeue(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    break;
                }

                batchNumber++;
                Logger.Info($"[InstrumentBackfillService] Processing batch {batchNumber} with {batch.Count} instruments");

                // Process the batch in parallel
                var tasks = batch.Select(item => ProcessBackfillItem(item)).ToArray();
                await Task.WhenAll(tasks);

                totalProcessed += batch.Count;
                Logger.Info($"[InstrumentBackfillService] Batch {batchNumber} complete. Total processed: {totalProcessed}");

                // Wait between batches to avoid rate limiting
                if (!_backfillQueue.IsEmpty)
                {
                    Logger.Info($"[InstrumentBackfillService] Waiting {HISTORICAL_BATCH_DELAY_MS / 1000}s before next batch...");
                    await Task.Delay(HISTORICAL_BATCH_DELAY_MS);
                }
            }

            _isProcessing = false;
            Logger.Info($"[InstrumentBackfillService] Completed all batches. Total instruments processed: {totalProcessed}");

            // Notify completion
            BackfillComplete?.Invoke();
        }

        /// <summary>
        /// Process a single backfill item - fetch historical data and cache it
        /// </summary>
        private async Task ProcessBackfillItem(BackfillItem item)
        {
            // Use zerodhaSymbol for API calls (the correct format from DB), ntSymbol for UI updates
            string apiSymbol = item.MappedInstrument.zerodhaSymbol ?? item.NTSymbol;
            Logger.Info($"[InstrumentBackfillService] Processing backfill for {item.NTSymbol} (apiSymbol={apiSymbol})");

            try
            {
                DateTime end = DateTime.Now;
                DateTime start = end.AddDays(-3);

                Logger.Debug($"[InstrumentBackfillService] Requesting 1-Minute historical data for {apiSymbol}...");

                var historicalDataService = HistoricalDataService.Instance;
                var records = await historicalDataService.GetHistoricalTrades(
                    BarsPeriodType.Minute,
                    apiSymbol,
                    start,
                    end,
                    MarketType.UsdM,
                    null
                );

                if (records != null && records.Count > 0)
                {
                    Logger.Info($"[InstrumentBackfillService] Received {records.Count} bars for {item.NTSymbol}");

                    // Notify status update
                    BackfillStatusChanged?.Invoke(item.NTSymbol, $"Cached ({records.Count})");

                    // Track this instrument
                    _processedInstruments.Add((item.NTSymbol, item.NTInstrument));

                    // Notify ready for streaming
                    InstrumentReadyForStreaming?.Invoke(item);

                    // Cache bars for straddle computation if this is an option
                    if (!string.IsNullOrEmpty(item.MappedInstrument.option_type) && item.MappedInstrument.strike.HasValue)
                    {
                        string straddleSymbol = CacheOptionBarsForStraddle(item.MappedInstrument, apiSymbol, records);
                        if (!string.IsNullOrEmpty(straddleSymbol))
                        {
                            _processedStraddleSymbols.Add(straddleSymbol);
                        }
                    }

                    // Extract last price and update UI - BUT only if data is recent!
                    var lastRecord = records.OrderByDescending(r => r.TimeStamp).FirstOrDefault();
                    if (lastRecord != null && lastRecord.Close > 0)
                    {
                        var dataAge = DateTime.Now - lastRecord.TimeStamp;
                        Logger.Debug($"[InstrumentBackfillService] {item.NTSymbol}: LastPrice={lastRecord.Close} from {lastRecord.TimeStamp:yyyy-MM-dd HH:mm} (age={dataAge.TotalMinutes:F1}min)");

                        // Only use historical price if it's within last 5 minutes
                        if (dataAge.TotalMinutes <= 5)
                        {
                            PriceUpdated?.Invoke(item.NTSymbol, lastRecord.Close);
                        }
                        else
                        {
                            Logger.Debug($"[InstrumentBackfillService] Skipping stale price update for {item.NTSymbol} (age={dataAge.TotalMinutes:F1}min > 5min threshold)");
                        }
                    }
                }
                else
                {
                    Logger.Warn($"[InstrumentBackfillService] No historical data received for {apiSymbol}");
                    BackfillStatusChanged?.Invoke(item.NTSymbol, "No Data");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[InstrumentBackfillService] ProcessBackfillItem({item.NTSymbol}): Exception occurred - {ex.Message}", ex);
                BackfillStatusChanged?.Invoke(item.NTSymbol, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Caches option bar data for straddle computation.
        /// When both CE and PE bars are cached, StraddleBarCache combines them into STRDL bars.
        /// </summary>
        private string CacheOptionBarsForStraddle(MappedInstrument mappedInst, string apiSymbol, List<Record> records)
        {
            try
            {
                // Build straddle symbol: {underlying}{expiry}{strike}_STRDL
                string expiryStr = mappedInst.expiry?.ToString("yyMMM").ToUpper() ?? "";
                string strikeStr = mappedInst.strike?.ToString("0") ?? "";
                string straddleSymbol = $"{mappedInst.underlying}{expiryStr}{strikeStr}_STRDL";

                Logger.Debug($"[InstrumentBackfillService] CacheOptionBarsForStraddle: {apiSymbol} -> {straddleSymbol} ({mappedInst.option_type})");

                if (mappedInst.option_type?.ToUpper() == "CE")
                {
                    StraddleBarCache.Instance.StoreCEBars(straddleSymbol, apiSymbol, records);
                }
                else if (mappedInst.option_type?.ToUpper() == "PE")
                {
                    StraddleBarCache.Instance.StorePEBars(straddleSymbol, apiSymbol, records);
                }

                return straddleSymbol;
            }
            catch (Exception ex)
            {
                Logger.Error($"[InstrumentBackfillService] CacheOptionBarsForStraddle: Error caching bars - {ex.Message}", ex);
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Item representing an instrument queued for backfill
    /// </summary>
    public class BackfillItem
    {
        public MappedInstrument MappedInstrument { get; set; }
        public string NTSymbol { get; set; }
        public Instrument NTInstrument { get; set; }
    }
}
