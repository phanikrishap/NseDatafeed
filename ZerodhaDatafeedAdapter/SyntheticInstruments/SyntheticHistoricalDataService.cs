using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Services.MarketData;
using ZerodhaDatafeedAdapter.ViewModels;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// Service for generating synthetic straddle historical data by combining leg OHLC bars.
    ///
    /// Approach:
    /// - For HISTORICAL bars: Combine OHLC using approximation (CE.Open + PE.Open, etc.)
    ///   Note: High/Low are upper bounds since individual highs may occur at different times
    /// - For LIVE bars: Real-time tick accumulation provides accurate High/Low
    /// </summary>
    public class SyntheticHistoricalDataService
    {
        private static SyntheticHistoricalDataService _instance;
        private readonly HistoricalDataService _historicalService;

        /// <summary>
        /// Gets the singleton instance of the SyntheticHistoricalDataService
        /// </summary>
        public static SyntheticHistoricalDataService Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SyntheticHistoricalDataService();
                return _instance;
            }
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private SyntheticHistoricalDataService()
        {
            _historicalService = HistoricalDataService.Instance;
        }


        /// <summary>
        /// Gets historical data for a synthetic straddle by combining CE and PE leg data.
        /// First checks SQLite cache (populated by SubscriptionManager during OptionChain data fetch),
        /// then falls back to fetching from Zerodha API if cache is empty.
        /// </summary>
        /// <param name="barsPeriodType">The bars period type (Day, Minute, etc.)</param>
        /// <param name="syntheticSymbol">The synthetic straddle symbol (e.g., SENSEX25DEC85000_STRDL)</param>
        /// <param name="ceSymbol">The CE option symbol</param>
        /// <param name="peSymbol">The PE option symbol</param>
        /// <param name="fromDate">The start date</param>
        /// <param name="toDate">The end date</param>
        /// <param name="viewModelBase">The view model for progress updates</param>
        /// <returns>A list of combined OHLC records</returns>
        public async Task<List<Record>> GetSyntheticHistoricalData(
            BarsPeriodType barsPeriodType,
            string syntheticSymbol,
            string ceSymbol,
            string peSymbol,
            DateTime fromDate,
            DateTime toDate,
            ViewModelBase viewModelBase)
        {
            Logger.Info($"[SyntheticHistorical] Fetching data for {syntheticSymbol} (CE={ceSymbol}, PE={peSymbol})");
            Logger.Info($"[SyntheticHistorical] Period: {barsPeriodType}, From: {fromDate}, To: {toDate}");

            // First, try to get cached data from SQLite (populated by OptionChain window)
            var cachedRecords = StraddleBarCache.Instance.GetCachedBars(syntheticSymbol, fromDate, toDate);
            if (cachedRecords != null && cachedRecords.Count > 0)
            {
                Logger.Info($"[SyntheticHistorical] Found {cachedRecords.Count} cached bars for {syntheticSymbol}");
                return cachedRecords;
            }

            Logger.Info($"[SyntheticHistorical] No cached data, fetching from API...");

            List<Record> combinedRecords = new List<Record>();

            try
            {
                // Fetch historical data for both legs in parallel
                // Note: SENSEX options are on BFO (BSE F&O), we use Futures as the closest match
                var ceTask = _historicalService.GetHistoricalTrades(
                    barsPeriodType, ceSymbol, fromDate, toDate, MarketType.Futures, viewModelBase);
                var peTask = _historicalService.GetHistoricalTrades(
                    barsPeriodType, peSymbol, fromDate, toDate, MarketType.Futures, viewModelBase);

                await Task.WhenAll(ceTask, peTask);

                var ceRecords = ceTask.Result;
                var peRecords = peTask.Result;

                Logger.Info($"[SyntheticHistorical] Fetched {ceRecords?.Count ?? 0} CE bars, {peRecords?.Count ?? 0} PE bars");

                if (ceRecords == null || peRecords == null || ceRecords.Count == 0 || peRecords.Count == 0)
                {
                    Logger.Warn($"[SyntheticHistorical] No data for one or both legs");
                    return combinedRecords;
                }

                // Build lookup dictionaries for both legs by normalized timestamp
                var ceByTimestamp = new Dictionary<DateTime, Record>();
                var peByTimestamp = new Dictionary<DateTime, Record>();

                foreach (var ce in ceRecords)
                {
                    var normalizedTime = NormalizeToMinute(ce.TimeStamp);
                    if (!ceByTimestamp.ContainsKey(normalizedTime))
                        ceByTimestamp[normalizedTime] = ce;
                }

                foreach (var pe in peRecords)
                {
                    var normalizedTime = NormalizeToMinute(pe.TimeStamp);
                    if (!peByTimestamp.ContainsKey(normalizedTime))
                        peByTimestamp[normalizedTime] = pe;
                }

                // Get all unique timestamps from both legs, sorted
                var allTimestamps = ceByTimestamp.Keys.Union(peByTimestamp.Keys).OrderBy(t => t).ToList();

                // Find first timestamp where BOTH legs have data (common starting point)
                Record lastKnownCE = null;
                Record lastKnownPE = null;
                DateTime? firstCompleteTimestamp = null;

                foreach (var ts in allTimestamps)
                {
                    if (ceByTimestamp.TryGetValue(ts, out var ce)) lastKnownCE = ce;
                    if (peByTimestamp.TryGetValue(ts, out var pe)) lastKnownPE = pe;

                    if (lastKnownCE != null && lastKnownPE != null)
                    {
                        firstCompleteTimestamp = ts;
                        break;
                    }
                }

                if (!firstCompleteTimestamp.HasValue)
                {
                    Logger.Warn($"[SyntheticHistorical] No common starting point for CE and PE");
                    return combinedRecords;
                }

                // Reset for combining pass
                lastKnownCE = null;
                lastKnownPE = null;
                int forwardFilledCount = 0;

                // Combine using forward-fill from common starting point
                foreach (var ts in allTimestamps)
                {
                    ceByTimestamp.TryGetValue(ts, out var ce);
                    peByTimestamp.TryGetValue(ts, out var pe);

                    // Update last known values
                    if (ce != null) lastKnownCE = ce;
                    if (pe != null) lastKnownPE = pe;

                    // Only start combining from first complete timestamp
                    if (ts < firstCompleteTimestamp.Value)
                        continue;

                    // Use current bar if available, otherwise use last known (forward-fill)
                    var ceToUse = ce ?? lastKnownCE;
                    var peToUse = pe ?? lastKnownPE;

                    if (ceToUse != null && peToUse != null)
                    {
                        if (ce == null || pe == null) forwardFilledCount++;

                        var combined = new Record
                        {
                            TimeStamp = ts,
                            Open = ceToUse.Open + peToUse.Open,
                            High = ceToUse.High + peToUse.High,      // Upper bound approximation
                            Low = ceToUse.Low + peToUse.Low,          // Lower bound approximation
                            Close = ceToUse.Close + peToUse.Close,
                            Volume = (ce?.Volume ?? 0) + (pe?.Volume ?? 0) // Only count actual volume
                        };

                        combinedRecords.Add(combined);
                    }
                }

                Logger.Info($"[SyntheticHistorical] Combined {combinedRecords.Count} bars ({forwardFilledCount} forward-filled) from {firstCompleteTimestamp.Value:HH:mm}");

                // Sort by timestamp
                combinedRecords = combinedRecords.OrderBy(r => r.TimeStamp).ToList();
            }
            catch (Exception ex)
            {
                Logger.Error($"[SyntheticHistorical] Error fetching data for {syntheticSymbol}: {ex.Message}");
            }

            return combinedRecords;
        }

        /// <summary>
        /// Normalizes a timestamp to the minute boundary for matching
        /// </summary>
        private DateTime NormalizeToMinute(DateTime timestamp)
        {
            return new DateTime(timestamp.Year, timestamp.Month, timestamp.Day,
                               timestamp.Hour, timestamp.Minute, 0, timestamp.Kind);
        }

        /// <summary>
        /// Checks if a symbol is a synthetic straddle symbol
        /// </summary>
        public static bool IsSyntheticSymbol(string symbol)
        {
            return !string.IsNullOrEmpty(symbol) && symbol.EndsWith("_STRDL", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses a synthetic straddle symbol to extract underlying info
        /// Example: SENSEX25DEC85000_STRDL -> underlying=SENSEX, expiry=25DEC, strike=85000
        /// </summary>
        public static (string underlying, string expiry, double strike) ParseSyntheticSymbol(string syntheticSymbol)
        {
            if (string.IsNullOrEmpty(syntheticSymbol) || !syntheticSymbol.EndsWith("_STRDL"))
                return (null, null, 0);

            // Remove _STRDL suffix
            string core = syntheticSymbol.Substring(0, syntheticSymbol.Length - 6);

            // Pattern: UNDERLYING + EXPIRY + STRIKE
            // Examples: SENSEX25DEC85000, NIFTY25DEC24500, BANKNIFTY25DEC51000

            // Find where the numeric strike starts (last contiguous digits)
            int strikeStart = core.Length;
            while (strikeStart > 0 && char.IsDigit(core[strikeStart - 1]))
            {
                strikeStart--;
            }

            if (strikeStart == core.Length)
                return (null, null, 0);

            string strikeStr = core.Substring(strikeStart);
            string prefix = core.Substring(0, strikeStart);

            // Extract expiry (last 5 chars before strike: YYMM or similar)
            // Typical format: 25DEC (2-digit year + 3-char month)
            if (prefix.Length >= 5)
            {
                string expiry = prefix.Substring(prefix.Length - 5);
                string underlying = prefix.Substring(0, prefix.Length - 5);

                if (double.TryParse(strikeStr, out double strike))
                {
                    return (underlying, expiry, strike);
                }
            }

            return (null, null, 0);
        }
    }
}
