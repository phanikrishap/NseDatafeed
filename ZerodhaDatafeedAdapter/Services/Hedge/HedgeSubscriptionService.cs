using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.MarketData;

namespace ZerodhaDatafeedAdapter.Services.Hedge
{
    /// <summary>
    /// Service responsible for subscribing to liquid hedge strikes BEYOND what Option Chain covers.
    ///
    /// Option Chain already subscribes to ATM ± 30 strikes at standard intervals (50 for NIFTY).
    /// This service subscribes to ADDITIONAL strikes at liquid intervals:
    /// - NIFTY: Multiples of 500 beyond the ±1500 range covered by Option Chain
    /// - SENSEX: Multiples of 500 beyond the ±1500 range covered by Option Chain
    ///
    /// Purpose: Have price data for far OTM liquid hedges when the hedge engine needs them.
    /// </summary>
    public class HedgeSubscriptionService
    {
        #region Singleton

        private static readonly Lazy<HedgeSubscriptionService> _instance =
            new Lazy<HedgeSubscriptionService>(() => new HedgeSubscriptionService());

        public static HedgeSubscriptionService Instance => _instance.Value;

        private HedgeSubscriptionService() { }

        #endregion

        #region Constants

        /// <summary>
        /// Hedge interval for both NIFTY and SENSEX (multiples of 500 are most liquid for hedges)
        /// </summary>
        public const decimal HEDGE_INTERVAL = 500m;

        /// <summary>
        /// Option Chain already covers ATM ± this many points (30 strikes × 50 = 1500 for NIFTY)
        /// We subscribe to strikes BEYOND this range
        /// </summary>
        public const decimal OPTION_CHAIN_COVERAGE = 1500m;

        /// <summary>
        /// How far beyond Option Chain coverage to subscribe (in points)
        /// E.g., 3000 means subscribe from ±1500 to ±4500 from ATM
        /// </summary>
        public const decimal HEDGE_RANGE_BEYOND_CHAIN = 3000m;

        #endregion

        #region Fields

        private readonly ConcurrentDictionary<string, HedgeQuote> _hedgeQuotes =
            new ConcurrentDictionary<string, HedgeQuote>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, bool> _subscribedSymbols =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private bool _isInitialized = false;
        private string _currentUnderlying;
        private DateTime _currentExpiry;
        private bool _isMonthlyExpiry;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the service has been initialized
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Current underlying being tracked
        /// </summary>
        public string CurrentUnderlying => _currentUnderlying;

        /// <summary>
        /// Number of hedge strikes currently subscribed
        /// </summary>
        public int SubscribedCount => _subscribedSymbols.Count;

        /// <summary>
        /// Get all current hedge quotes (thread-safe copy)
        /// </summary>
        public IReadOnlyDictionary<string, HedgeQuote> GetAllQuotes()
        {
            return new Dictionary<string, HedgeQuote>(_hedgeQuotes);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize hedge subscriptions for the given underlying and expiry.
        /// Subscribes to liquid hedge strikes (multiples of 500) BEYOND Option Chain coverage.
        /// </summary>
        /// <param name="underlying">Underlying symbol (NIFTY, SENSEX, BANKNIFTY)</param>
        /// <param name="expiry">Expiry date for the options</param>
        /// <param name="atmStrike">Current ATM strike (center point for subscriptions)</param>
        /// <param name="isMonthlyExpiry">Whether this is a monthly expiry</param>
        public async Task InitializeAsync(string underlying, DateTime expiry, decimal atmStrike, bool isMonthlyExpiry)
        {
            if (string.IsNullOrEmpty(underlying))
            {
                TBSLogger.Warn("[HedgeSubscription] Cannot initialize with empty underlying");
                return;
            }

            TBSLogger.Info($"[HedgeSubscription] Initializing for {underlying}, Expiry={expiry:yyyy-MM-dd}, ATM={atmStrike}, Monthly={isMonthlyExpiry}");

            _currentUnderlying = underlying.ToUpperInvariant();
            _currentExpiry = expiry;
            _isMonthlyExpiry = isMonthlyExpiry;

            // Clear existing subscriptions if underlying changed
            if (_subscribedSymbols.Count > 0)
            {
                TBSLogger.Info($"[HedgeSubscription] Clearing {_subscribedSymbols.Count} existing subscriptions");
                _hedgeQuotes.Clear();
                _subscribedSymbols.Clear();
            }

            // Round ATM strike to nearest hedge interval (500)
            decimal roundedAtm = RoundToInterval(atmStrike, HEDGE_INTERVAL);
            TBSLogger.Info($"[HedgeSubscription] Rounded ATM from {atmStrike} to {roundedAtm}");

            // Generate hedge strikes BEYOND Option Chain coverage
            // Option Chain covers ATM ± 1500, so we subscribe from ±1500 to ±4500
            var hedgeStrikes = GenerateHedgeStrikesBeyondChain(roundedAtm);
            TBSLogger.Info($"[HedgeSubscription] Generated {hedgeStrikes.Count} far OTM hedge strikes (beyond Option Chain range)");

            if (hedgeStrikes.Count > 0)
            {
                TBSLogger.Info($"[HedgeSubscription] Strike range: {hedgeStrikes.Min()} to {hedgeStrikes.Max()}");
            }

            // Subscribe to all hedge strikes (CE and PE)
            int subscribed = await SubscribeToHedgeStrikesAsync(hedgeStrikes);

            _isInitialized = true;
            TBSLogger.Info($"[HedgeSubscription] Initialization complete. Subscribed to {subscribed} symbols for far OTM hedges");
        }

        /// <summary>
        /// Update ATM strike and subscribe to new hedge strikes if needed.
        /// Called when spot price moves significantly.
        /// </summary>
        public async Task UpdateAtmStrikeAsync(decimal newAtmStrike)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_currentUnderlying))
            {
                TBSLogger.Warn("[HedgeSubscription] Cannot update ATM - not initialized");
                return;
            }

            decimal roundedAtm = RoundToInterval(newAtmStrike, HEDGE_INTERVAL);

            // Generate new strikes beyond Option Chain coverage
            var newStrikes = GenerateHedgeStrikesBeyondChain(roundedAtm);

            // Find strikes not yet subscribed
            var strikesToSubscribe = new List<decimal>();
            foreach (var strike in newStrikes)
            {
                string ceSymbol = BuildHedgeSymbol(strike, "CE");
                string peSymbol = BuildHedgeSymbol(strike, "PE");

                if (!_subscribedSymbols.ContainsKey(ceSymbol) || !_subscribedSymbols.ContainsKey(peSymbol))
                {
                    strikesToSubscribe.Add(strike);
                }
            }

            if (strikesToSubscribe.Count > 0)
            {
                TBSLogger.Info($"[HedgeSubscription] ATM moved. Subscribing to {strikesToSubscribe.Count} new far OTM hedge strikes");
                await SubscribeToHedgeStrikesAsync(strikesToSubscribe);
            }
        }

        /// <summary>
        /// Get the current quote for a hedge symbol.
        /// </summary>
        public HedgeQuote GetQuote(string symbol)
        {
            _hedgeQuotes.TryGetValue(symbol, out var quote);
            return quote;
        }

        /// <summary>
        /// Get quotes for a specific strike (both CE and PE).
        /// </summary>
        public (HedgeQuote ce, HedgeQuote pe) GetQuotesForStrike(decimal strike)
        {
            var ceSymbol = BuildHedgeSymbol(strike, "CE");
            var peSymbol = BuildHedgeSymbol(strike, "PE");

            _hedgeQuotes.TryGetValue(ceSymbol, out var ceQuote);
            _hedgeQuotes.TryGetValue(peSymbol, out var peQuote);

            return (ceQuote, peQuote);
        }

        /// <summary>
        /// Get all CE quotes within a price range, sorted by premium.
        /// Useful for hedge selection.
        /// </summary>
        public List<HedgeQuote> GetCEQuotesInRange(decimal minPremium, decimal maxPremium)
        {
            return _hedgeQuotes.Values
                .Where(q => q.OptionType == "CE" && q.LastPrice >= minPremium && q.LastPrice <= maxPremium)
                .OrderBy(q => q.LastPrice)
                .ToList();
        }

        /// <summary>
        /// Get all PE quotes within a price range, sorted by premium.
        /// </summary>
        public List<HedgeQuote> GetPEQuotesInRange(decimal minPremium, decimal maxPremium)
        {
            return _hedgeQuotes.Values
                .Where(q => q.OptionType == "PE" && q.LastPrice >= minPremium && q.LastPrice <= maxPremium)
                .OrderBy(q => q.LastPrice)
                .ToList();
        }

        /// <summary>
        /// Find the best hedge (cheapest option above a minimum premium).
        /// </summary>
        public HedgeQuote FindBestHedge(string optionType, decimal minPremium, decimal maxPremium = decimal.MaxValue)
        {
            return _hedgeQuotes.Values
                .Where(q => q.OptionType == optionType && q.LastPrice >= minPremium && q.LastPrice <= maxPremium && q.LastPrice > 0)
                .OrderBy(q => q.LastPrice)
                .FirstOrDefault();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Round a strike to the nearest hedge interval.
        /// </summary>
        private decimal RoundToInterval(decimal strike, decimal interval)
        {
            return Math.Round(strike / interval) * interval;
        }

        /// <summary>
        /// Generate list of hedge strikes BEYOND Option Chain coverage.
        /// Option Chain covers ATM ± 1500 points, so we subscribe to strikes
        /// from ±1500 to ±4500 (at 500-point intervals).
        ///
        /// For NIFTY at ATM 24000:
        /// - Option Chain covers: 22500 to 25500 (±1500)
        /// - Hedge service covers: 19500-22000 (lower) and 26000-28500 (upper)
        /// - That's 6 strikes on each side × 2 (CE+PE) = 24 symbols
        /// </summary>
        private List<decimal> GenerateHedgeStrikesBeyondChain(decimal atmStrike)
        {
            var strikes = new List<decimal>();

            // Lower side: from (ATM - COVERAGE - RANGE) to (ATM - COVERAGE)
            // E.g., for ATM=24000: 19500, 20000, 20500, 21000, 21500, 22000
            decimal lowerStart = atmStrike - OPTION_CHAIN_COVERAGE - HEDGE_RANGE_BEYOND_CHAIN;
            decimal lowerEnd = atmStrike - OPTION_CHAIN_COVERAGE;

            for (decimal s = lowerStart; s <= lowerEnd; s += HEDGE_INTERVAL)
            {
                if (s > 0)
                    strikes.Add(s);
            }

            // Upper side: from (ATM + COVERAGE) to (ATM + COVERAGE + RANGE)
            // E.g., for ATM=24000: 25500, 26000, 26500, 27000, 27500, 28000
            decimal upperStart = atmStrike + OPTION_CHAIN_COVERAGE;
            decimal upperEnd = atmStrike + OPTION_CHAIN_COVERAGE + HEDGE_RANGE_BEYOND_CHAIN;

            for (decimal s = upperStart; s <= upperEnd; s += HEDGE_INTERVAL)
            {
                strikes.Add(s);
            }

            return strikes.OrderBy(s => s).ToList();
        }

        /// <summary>
        /// Subscribe to hedge strikes (both CE and PE for each strike).
        /// </summary>
        private async Task<int> SubscribeToHedgeStrikesAsync(List<decimal> strikes)
        {
            int subscribedCount = 0;
            var marketDataService = MarketDataService.Instance;
            var instrumentManager = InstrumentManager.Instance;

            foreach (var strike in strikes)
            {
                // Subscribe to CE
                if (await SubscribeToHedgeOptionAsync(strike, "CE", marketDataService, instrumentManager))
                    subscribedCount++;

                // Subscribe to PE
                if (await SubscribeToHedgeOptionAsync(strike, "PE", marketDataService, instrumentManager))
                    subscribedCount++;
            }

            return subscribedCount;
        }

        /// <summary>
        /// Subscribe to a single hedge option.
        /// </summary>
        private async Task<bool> SubscribeToHedgeOptionAsync(
            decimal strike,
            string optionType,
            MarketDataService marketDataService,
            InstrumentManager instrumentManager)
        {
            try
            {
                // Build symbol
                string symbol = BuildHedgeSymbol(strike, optionType);

                // Skip if already subscribed
                if (_subscribedSymbols.ContainsKey(symbol))
                    return false;

                // Get instrument token from DB
                string segment = _currentUnderlying.Contains("SENSEX") || _currentUnderlying.Contains("BANKEX")
                    ? "BFO-OPT"
                    : "NFO-OPT";

                var (token, tradingSymbol) = instrumentManager.LookupOptionDetailsInSqlite(
                    segment,
                    _currentUnderlying,
                    _currentExpiry.ToString("yyyy-MM-dd"),
                    (double)strike,
                    optionType);

                if (token <= 0)
                {
                    TBSLogger.Debug($"[HedgeSubscription] No token found for {symbol}");
                    return false;
                }

                // Create quote object
                var quote = new HedgeQuote
                {
                    Symbol = symbol,
                    TradingSymbol = tradingSymbol ?? symbol,
                    Strike = strike,
                    OptionType = optionType,
                    InstrumentToken = token,
                    Underlying = _currentUnderlying,
                    Expiry = _currentExpiry
                };

                // Subscribe via direct callback (no UI required)
                bool subscribed = await marketDataService.SubscribeToSymbolDirectAsync(
                    symbol,
                    (int)token,
                    isIndex: false,
                    onTick: (price, volume, timestamp) => OnHedgeTick(symbol, price, volume, timestamp));

                if (subscribed)
                {
                    _hedgeQuotes[symbol] = quote;
                    _subscribedSymbols[symbol] = true;
                    TBSLogger.Debug($"[HedgeSubscription] Subscribed to {symbol} (token={token})");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                TBSLogger.Error($"[HedgeSubscription] Error subscribing to {strike} {optionType}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Build option symbol for hedge.
        /// </summary>
        private string BuildHedgeSymbol(decimal strike, string optionType)
        {
            return SymbolHelper.BuildOptionSymbol(
                _currentUnderlying,
                _currentExpiry,
                strike,
                optionType,
                _isMonthlyExpiry);
        }

        /// <summary>
        /// Handle tick data for hedge symbols.
        /// </summary>
        private void OnHedgeTick(string symbol, double price, long volume, DateTime timestamp)
        {
            if (_hedgeQuotes.TryGetValue(symbol, out var quote))
            {
                quote.LastPrice = (decimal)price;
                quote.Volume = volume;
                quote.LastUpdate = timestamp;
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents a quote for a hedge option.
    /// </summary>
    public class HedgeQuote
    {
        public string Symbol { get; set; }
        public string TradingSymbol { get; set; }
        public decimal Strike { get; set; }
        public string OptionType { get; set; }
        public long InstrumentToken { get; set; }
        public string Underlying { get; set; }
        public DateTime Expiry { get; set; }

        public decimal LastPrice { get; set; }
        public long Volume { get; set; }
        public DateTime LastUpdate { get; set; }

        public override string ToString()
        {
            return $"{Symbol}: {LastPrice:F2} (Vol={Volume}, Updated={LastUpdate:HH:mm:ss})";
        }
    }
}
