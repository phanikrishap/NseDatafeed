using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Instruments;

using MappedInstrument = ZerodhaDatafeedAdapter.Models.MappedInstrument;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Data class for a single ticker row in the Market Analyzer
    /// </summary>
    public class TickerData : INotifyPropertyChanged
    {
        private string _symbol;
        private double _lastPrice;
        private double _netChange;
        private double _netChangePercent;
        private double _open;
        private double _high;
        private double _low;
        private double _close;
        private double _projectedOpen;
        private DateTime _lastUpdateTime;
        private string _expiry = "---";

        public string Symbol
        {
            get => _symbol;
            set { _symbol = value; OnPropertyChanged(); }
        }

        public double LastPrice
        {
            get => _lastPrice;
            set { _lastPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastPriceDisplay)); }
        }

        public string LastPriceDisplay => LastPrice > 0 ? LastPrice.ToString("F2") : "---";

        public double NetChange
        {
            get => _netChange;
            set { _netChange = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangeDisplay)); }
        }

        public string NetChangeDisplay => NetChange != 0 ? $"{NetChange:+0.00;-0.00;0.00}" : "---";

        public double NetChangePercent
        {
            get => _netChangePercent;
            set { _netChangePercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetChangePercentDisplay)); }
        }

        public string NetChangePercentDisplay => NetChangePercent != 0 ? $"{NetChangePercent:+0.00;-0.00;0.00}%" : "---";

        public double Open
        {
            get => _open;
            set { _open = value; OnPropertyChanged(); }
        }

        public double High
        {
            get => _high;
            set { _high = value; OnPropertyChanged(); }
        }

        public double Low
        {
            get => _low;
            set { _low = value; OnPropertyChanged(); }
        }

        public double Close
        {
            get => _close;
            set { _close = value; OnPropertyChanged(); }
        }

        public double ProjectedOpen
        {
            get => _projectedOpen;
            set { _projectedOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProjectedOpenDisplay)); }
        }

        public string ProjectedOpenDisplay => ProjectedOpen > 0 ? ProjectedOpen.ToString("F0") : "---";

        public DateTime LastUpdateTime
        {
            get => _lastUpdateTime;
            set { _lastUpdateTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastUpdateTimeDisplay)); }
        }

        public string LastUpdateTimeDisplay => _lastUpdateTime != DateTime.MinValue ? _lastUpdateTime.ToString("HH:mm:ss") : "---";

        public string Expiry
        {
            get => _expiry;
            set { _expiry = value; OnPropertyChanged(); }
        }

        public bool IsPositive => NetChange >= 0;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Core logic engine for Market Analyzer - calculates projected opens and generates option symbols
    /// </summary>
    public class MarketAnalyzerLogic
    {
        private static MarketAnalyzerLogic _instance;
        public static MarketAnalyzerLogic Instance => _instance ?? (_instance = new MarketAnalyzerLogic());

        // Hardcoded symbols for now as per requirements
        private const string SYMBOL_GIFT_NIFTY = "GIFT_NIFTY";
        private const string SYMBOL_NIFTY_SPOT = "NIFTY";
        private const string SYMBOL_SENSEX_SPOT = "SENSEX";
        private const string SYMBOL_O_NIFTY = "NIFTY"; // Underlying name in DB
        private const string SYMBOL_O_SENSEX = "SENSEX";

        // State
        public double GiftNiftyPrice { get; private set; }
        public double NiftySpotPrice { get; private set; }
        public double SensexSpotPrice { get; private set; }

        // Current dynamic instrument list for straddles
        private InstrumentList _currentInstrumentList;
        public InstrumentList CurrentStraddleList => _currentInstrumentList;
        public double GiftNiftyPriorClose { get; private set; }

        // Ticker data for UI binding
        public TickerData GiftNiftyTicker { get; } = new TickerData { Symbol = "GIFT NIFTY" };
        public TickerData NiftyTicker { get; } = new TickerData { Symbol = "NIFTY" };
        public TickerData SensexTicker { get; } = new TickerData { Symbol = "SENSEX" };

        // Events
        public event Action<string> StatusUpdated;
        public event Action<List<MappedInstrument>> OptionsGenerated;
        public event Action<TickerData> TickerUpdated;
        public event Action<string, string> HistoricalDataStatusChanged; // symbol, status
        public event Action<string, decimal> ATMStrikeUpdated; // underlying, atmStrike

        /// <summary>
        /// Event fired when price data is ready (enough option prices have been received).
        /// This replaces the hardcoded 45-second delay for TBS initialization.
        /// Fires with (underlying, priceCount) when at least MIN_PRICES_FOR_READY symbols have prices.
        /// </summary>
        public event Action<string, int> PriceSyncReady;
        private bool _priceSyncFired = false;
        private const int MIN_PRICES_FOR_READY = 10; // Minimum option prices before considering data ready

        // Current ATM strike per underlying (set by Option Chain, consumed by TBS Manager)
        private readonly Dictionary<string, decimal> _currentATMStrikes = new Dictionary<string, decimal>();
        private readonly object _atmLock = new object();

        // ============================================================================
        // EXPIRY CACHE: Cached expiries per underlying for symbol generation
        // ============================================================================
        private readonly Dictionary<string, List<DateTime>> _cachedExpiries = new Dictionary<string, List<DateTime>>();
        private readonly object _expiryLock = new object();
        private string _selectedUnderlying;
        private DateTime? _selectedExpiry;
        private bool _selectedIsMonthlyExpiry;

        // ============================================================================
        // LOT SIZE CACHE: Cached lot sizes per underlying from instrument masters DB
        // ============================================================================
        private readonly Dictionary<string, int> _cachedLotSizes = new Dictionary<string, int>();
        private readonly object _lotSizeLock = new object();

        /// <summary>
        /// Get cached expiries for an underlying. Returns empty list if not cached.
        /// </summary>
        public List<DateTime> GetCachedExpiries(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return new List<DateTime>();
            lock (_expiryLock)
            {
                if (_cachedExpiries.TryGetValue(underlying.ToUpperInvariant(), out var expiries))
                    return new List<DateTime>(expiries); // Return copy
            }
            return new List<DateTime>();
        }

        /// <summary>
        /// Get cached lot size for an underlying. Returns 0 if not cached.
        /// </summary>
        public int GetCachedLotSize(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 0;
            lock (_lotSizeLock)
            {
                if (_cachedLotSizes.TryGetValue(underlying.ToUpperInvariant(), out var lotSize))
                    return lotSize;
            }
            return 0;
        }

        /// <summary>
        /// Get the currently selected underlying from Option Chain
        /// </summary>
        public string SelectedUnderlying => _selectedUnderlying;

        /// <summary>
        /// Get the currently selected expiry from Option Chain
        /// </summary>
        public DateTime? SelectedExpiry => _selectedExpiry;

        /// <summary>
        /// Whether the selected expiry is a monthly expiry
        /// </summary>
        public bool SelectedIsMonthlyExpiry => _selectedIsMonthlyExpiry;

        // ============================================================================
        // PRICE HUB: Centralized price cache to avoid duplicate WebSocket subscriptions
        // Option Chain populates this, TBS Manager and other consumers read from it
        // ============================================================================
        private readonly Dictionary<string, decimal> _priceHub = new Dictionary<string, decimal>();
        private readonly Dictionary<string, DateTime> _priceTimestamps = new Dictionary<string, DateTime>();
        private readonly object _priceHubLock = new object();

        /// <summary>
        /// Event fired when a price is updated in the hub (for consumers who want real-time updates)
        /// </summary>
        public event Action<string, decimal> PriceUpdated;

        /// <summary>
        /// Get current price for a symbol from the centralized price hub.
        /// Returns 0 if symbol not found or price not available.
        /// </summary>
        public decimal GetPrice(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return 0;
            lock (_priceHubLock)
            {
                if (_priceHub.TryGetValue(symbol, out decimal price))
                    return price;
            }
            return 0;
        }

        /// <summary>
        /// Get current price with timestamp for a symbol.
        /// </summary>
        public (decimal price, DateTime timestamp) GetPriceWithTimestamp(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return (0, DateTime.MinValue);
            lock (_priceHubLock)
            {
                if (_priceHub.TryGetValue(symbol, out decimal price))
                {
                    var timestamp = _priceTimestamps.TryGetValue(symbol, out DateTime ts) ? ts : DateTime.MinValue;
                    return (price, timestamp);
                }
            }
            return (0, DateTime.MinValue);
        }

        /// <summary>
        /// Update option price in the centralized hub (called by Option Chain or any WebSocket consumer).
        /// Only fires event if price actually changed.
        /// </summary>
        public void UpdateOptionPrice(string symbol, decimal price, DateTime? timestamp = null)
        {
            if (string.IsNullOrEmpty(symbol) || price <= 0) return;

            bool priceChanged = false;
            int priceCount = 0;
            lock (_priceHubLock)
            {
                if (!_priceHub.TryGetValue(symbol, out decimal existingPrice) || existingPrice != price)
                {
                    _priceHub[symbol] = price;
                    _priceTimestamps[symbol] = timestamp ?? DateTime.Now;
                    priceChanged = true;
                }
                priceCount = _priceHub.Count;
            }

            // Fire event outside lock to avoid deadlocks
            if (priceChanged)
            {
                PriceUpdated?.Invoke(symbol, price);

                // Check if we should fire PriceSyncReady (only once per session)
                CheckAndFirePriceSyncReady(priceCount);
            }
        }

        /// <summary>
        /// Check if enough prices have been received to fire PriceSyncReady event.
        /// Only fires once per session to avoid repeated notifications.
        /// </summary>
        private void CheckAndFirePriceSyncReady(int priceCount)
        {
            if (_priceSyncFired || priceCount < MIN_PRICES_FOR_READY) return;

            // Double-check with lock to prevent race condition
            lock (_priceHubLock)
            {
                if (_priceSyncFired) return;
                _priceSyncFired = true;
            }

            var underlying = _selectedUnderlying ?? "NIFTY";
            Logger.Info($"[MarketAnalyzerLogic] PriceSyncReady: {priceCount} option prices received for {underlying}");
            PriceSyncReady?.Invoke(underlying, priceCount);
        }

        /// <summary>
        /// Reset the PriceSyncReady flag (call when underlying changes or on new session)
        /// </summary>
        public void ResetPriceSyncReady()
        {
            lock (_priceHubLock)
            {
                _priceSyncFired = false;
            }
        }

        /// <summary>
        /// Bulk update prices (more efficient for batch updates from Option Chain)
        /// </summary>
        public void UpdatePrices(IEnumerable<(string symbol, decimal price)> updates)
        {
            var changedPrices = new List<(string symbol, decimal price)>();

            lock (_priceHubLock)
            {
                var now = DateTime.Now;
                foreach (var (symbol, price) in updates)
                {
                    if (string.IsNullOrEmpty(symbol) || price <= 0) continue;

                    if (!_priceHub.TryGetValue(symbol, out decimal existingPrice) || existingPrice != price)
                    {
                        _priceHub[symbol] = price;
                        _priceTimestamps[symbol] = now;
                        changedPrices.Add((symbol, price));
                    }
                }
            }

            // Fire events outside lock
            foreach (var (symbol, price) in changedPrices)
            {
                PriceUpdated?.Invoke(symbol, price);
            }
        }

        /// <summary>
        /// Get all cached prices (for debugging/diagnostics)
        /// </summary>
        public Dictionary<string, decimal> GetAllPrices()
        {
            lock (_priceHubLock)
            {
                return new Dictionary<string, decimal>(_priceHub);
            }
        }

        /// <summary>
        /// Get count of cached prices
        /// </summary>
        public int PriceCount
        {
            get
            {
                lock (_priceHubLock)
                {
                    return _priceHub.Count;
                }
            }
        }

        /// <summary>
        /// Get the current ATM strike for an underlying (as calculated by Option Chain)
        /// </summary>
        public decimal GetATMStrike(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 0;
            lock (_atmLock)
            {
                if (_currentATMStrikes.TryGetValue(underlying.ToUpperInvariant(), out decimal strike))
                    return strike;
            }
            return 0;
        }

        /// <summary>
        /// Set the current ATM strike for an underlying (called by Option Chain)
        /// </summary>
        public void SetATMStrike(string underlying, decimal strike)
        {
            if (string.IsNullOrEmpty(underlying) || strike <= 0) return;
            var key = underlying.ToUpperInvariant();
            lock (_atmLock)
            {
                _currentATMStrikes[key] = strike;
            }
            Logger.Debug($"[MarketAnalyzerLogic] ATM strike updated: {key} = {strike}");
            ATMStrikeUpdated?.Invoke(key, strike);
        }

        // Track if we've already generated options to avoid duplicates
        private bool _optionsAlreadyGenerated = false;

        // Lock object for straddles_config.json file access to prevent concurrent write errors
        private static readonly object _straddlesConfigLock = new object();

        private MarketAnalyzerLogic()
        {
            Logger.Info("[MarketAnalyzerLogic] Constructor: Initializing singleton instance");
        }

        /// <summary>
        /// Notify that historical data request status changed for a symbol
        /// </summary>
        public void NotifyHistoricalDataStatus(string symbol, string status)
        {
            Logger.Info($"[MarketAnalyzerLogic] NotifyHistoricalDataStatus: {symbol} = {status}");
            HistoricalDataStatusChanged?.Invoke(symbol, status);
        }

        public void UpdatePrice(string symbol, double price, double priorClose = 0, double open = 0, double high = 0, double low = 0, double close = 0)
        {
            Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): symbol='{symbol}', price={price}, priorClose={priorClose}");

            TickerData ticker = null;

            // Normalize symbol - handle "GIFT NIFTY" (with space) and "GIFT_NIFTY" (with underscore)
            string normalizedSymbol = symbol?.Replace(" ", "_").ToUpperInvariant() ?? "";

            if (normalizedSymbol.Equals(SYMBOL_GIFT_NIFTY, StringComparison.OrdinalIgnoreCase) ||
                symbol.Equals("GIFT NIFTY", StringComparison.OrdinalIgnoreCase))
            {
                GiftNiftyPrice = price;
                if (priorClose > 0) GiftNiftyPriorClose = priorClose;
                Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): GIFT_NIFTY updated - Price={GiftNiftyPrice}, PriorClose={GiftNiftyPriorClose}");

                ticker = GiftNiftyTicker;
                ticker.LastPrice = price;
                ticker.Open = open > 0 ? open : ticker.Open;
                ticker.High = high > 0 ? high : ticker.High;
                ticker.Low = low > 0 ? low : ticker.Low;
                ticker.Close = close > 0 ? close : (priorClose > 0 ? priorClose : ticker.Close);
                if (ticker.Close > 0)
                {
                    ticker.NetChange = price - ticker.Close;
                    ticker.NetChangePercent = (ticker.NetChange / ticker.Close) * 100;
                }
            }
            else if (normalizedSymbol.Equals(SYMBOL_NIFTY_SPOT, StringComparison.OrdinalIgnoreCase) ||
                     symbol.Equals("NIFTY 50", StringComparison.OrdinalIgnoreCase) ||
                     symbol.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
            {
                NiftySpotPrice = price;
                Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): NIFTY_SPOT updated - Price={NiftySpotPrice}");

                ticker = NiftyTicker;
                ticker.LastPrice = price;
                ticker.Open = open > 0 ? open : ticker.Open;
                ticker.High = high > 0 ? high : ticker.High;
                ticker.Low = low > 0 ? low : ticker.Low;
                ticker.Close = close > 0 ? close : ticker.Close;
                if (ticker.Close > 0)
                {
                    ticker.NetChange = price - ticker.Close;
                    ticker.NetChangePercent = (ticker.NetChange / ticker.Close) * 100;
                }
            }
            else if (normalizedSymbol.Equals(SYMBOL_SENSEX_SPOT, StringComparison.OrdinalIgnoreCase) ||
                     symbol.Equals("SENSEX", StringComparison.OrdinalIgnoreCase))
            {
                SensexSpotPrice = price;
                Logger.Debug($"[MarketAnalyzerLogic] UpdatePrice(): SENSEX_SPOT updated - Price={SensexSpotPrice}");

                ticker = SensexTicker;
                ticker.LastPrice = price;
                ticker.Open = open > 0 ? open : ticker.Open;
                ticker.High = high > 0 ? high : ticker.High;
                ticker.Low = low > 0 ? low : ticker.Low;
                ticker.Close = close > 0 ? close : ticker.Close;
                if (ticker.Close > 0)
                {
                    ticker.NetChange = price - ticker.Close;
                    ticker.NetChangePercent = (ticker.NetChange / ticker.Close) * 100;
                }
            }
            else
            {
                Logger.Warn($"[MarketAnalyzerLogic] UpdatePrice(): Unknown symbol '{symbol}' - ignoring");
                return;
            }

            // Fire ticker update event
            if (ticker != null)
            {
                ticker.LastUpdateTime = DateTime.Now;
                TickerUpdated?.Invoke(ticker);
            }

            CheckAndCalculate();
        }

        private void CheckAndCalculate()
        {
            Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): GIFT={GiftNiftyPrice}, NIFTY={NiftySpotPrice}, SENSEX={SensexSpotPrice}, PriorClose={GiftNiftyPriorClose}");

            // We need at least GIFT NIFTY and one Spot to proceed
            if (GiftNiftyPrice <= 0)
            {
                Logger.Debug("[MarketAnalyzerLogic] CheckAndCalculate(): GIFT NIFTY price not available yet, waiting...");
                return;
            }

            // Simple logic: If we have prices, try to calculate projected open
            double changePercent = 0.0;
            if (GiftNiftyPriorClose > 0)
            {
                changePercent = (GiftNiftyPrice - GiftNiftyPriorClose) / GiftNiftyPriorClose;
                Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): Change% = ({GiftNiftyPrice} - {GiftNiftyPriorClose}) / {GiftNiftyPriorClose} = {changePercent:P4}");
            }
            else
            {
                Logger.Warn("[MarketAnalyzerLogic] CheckAndCalculate(): GIFT NIFTY PriorClose not available, using 0% change");
                StatusUpdated?.Invoke($"Waiting for GIFT NIFTY Prior Close... (Current: {GiftNiftyPrice})");
            }

            // Calculate Projected Opens
            double niftyProjected = NiftySpotPrice > 0 ? NiftySpotPrice * (1 + changePercent) : 0;
            double sensexProjected = SensexSpotPrice > 0 ? SensexSpotPrice * (1 + changePercent) : 0;

            // Update ticker projected values
            NiftyTicker.ProjectedOpen = niftyProjected;
            SensexTicker.ProjectedOpen = sensexProjected;

            Logger.Debug($"[MarketAnalyzerLogic] CheckAndCalculate(): Projected Opens - NIFTY={niftyProjected:F2}, SENSEX={sensexProjected:F2}");
            StatusUpdated?.Invoke($"GIFT: {GiftNiftyPrice} ({changePercent:P2}) | Proj Nifty: {niftyProjected:F0} | Proj Sensex: {sensexProjected:F0}");

            if ((niftyProjected > 0 || sensexProjected > 0) && !_optionsAlreadyGenerated)
            {
                Logger.Info("[MarketAnalyzerLogic] CheckAndCalculate(): Triggering option generation...");
                GenerateOptions(niftyProjected, sensexProjected);
            }
            else if (_optionsAlreadyGenerated)
            {
                Logger.Debug("[MarketAnalyzerLogic] CheckAndCalculate(): Options already generated, skipping");
            }
        }

        private async void GenerateOptions(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Starting with niftyProjected={niftyProjected:F2}, sensexProjected={sensexProjected:F2}");

            try
            {
                // Mark as generated to prevent duplicate runs
                _optionsAlreadyGenerated = true;

                // FETCH EXPIRIES from database
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Fetching NIFTY expiries from database...");
                var niftyExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlying("NIFTY");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Found {niftyExpiries.Count} NIFTY expiries");

                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Fetching SENSEX expiries from database...");
                var sensexExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlying("SENSEX");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Found {sensexExpiries.Count} SENSEX expiries");

                // Cache expiries globally for TBS Manager and other consumers
                lock (_expiryLock)
                {
                    _cachedExpiries["NIFTY"] = new List<DateTime>(niftyExpiries);
                    _cachedExpiries["SENSEX"] = new List<DateTime>(sensexExpiries);
                }
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Cached expiries for NIFTY and SENSEX");

                // FETCH LOT SIZES from database and cache them
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Fetching lot sizes from database...");
                var niftyLotSize = await InstrumentManager.Instance.GetLotSizeForUnderlying("NIFTY");
                var sensexLotSize = await InstrumentManager.Instance.GetLotSizeForUnderlying("SENSEX");
                var bankniftyLotSize = await InstrumentManager.Instance.GetLotSizeForUnderlying("BANKNIFTY");
                var finniftyLotSize = await InstrumentManager.Instance.GetLotSizeForUnderlying("FINNIFTY");
                var midcpniftyLotSize = await InstrumentManager.Instance.GetLotSizeForUnderlying("MIDCPNIFTY");

                lock (_lotSizeLock)
                {
                    if (niftyLotSize > 0) _cachedLotSizes["NIFTY"] = niftyLotSize;
                    if (sensexLotSize > 0) _cachedLotSizes["SENSEX"] = sensexLotSize;
                    if (bankniftyLotSize > 0) _cachedLotSizes["BANKNIFTY"] = bankniftyLotSize;
                    if (finniftyLotSize > 0) _cachedLotSizes["FINNIFTY"] = finniftyLotSize;
                    if (midcpniftyLotSize > 0) _cachedLotSizes["MIDCPNIFTY"] = midcpniftyLotSize;
                }
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Cached lot sizes - NIFTY={niftyLotSize}, SENSEX={sensexLotSize}, BANKNIFTY={bankniftyLotSize}");

                var niftyNear = GetNearestExpiry(niftyExpiries);
                var sensexNear = GetNearestExpiry(sensexExpiries);

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Nearest expiries - NIFTY={niftyNear:yyyy-MM-dd}, SENSEX={sensexNear:yyyy-MM-dd}");

                string selectedUnderlying = "NIFTY";
                DateTime selectedExpiry = niftyNear;
                double projectedPrice = niftyProjected;
                int stepSize = 50;

                double sensexDTE = (sensexNear.Date - DateTime.Today).TotalDays;
                double niftyDTE = (niftyNear.Date - DateTime.Today).TotalDays;

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): DTE calculations - NIFTY DTE={niftyDTE}, SENSEX DTE={sensexDTE}");

                // Priority Rules based on DTE:
                // 1. NIFTY DTE = 0 (0DTE)
                // 2. SENSEX DTE = 0
                // 3. NIFTY DTE = 1 (1DTE)
                // 4. SENSEX DTE = 1
                // 5. Default: NIFTY

                if (niftyDTE == 0)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected NIFTY 0DTE (Priority 1)");
                }
                else if (sensexDTE == 0)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected SENSEX 0DTE (Priority 2)");
                }
                else if (niftyDTE == 1)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected NIFTY 1DTE (Priority 3)");
                }
                else if (sensexDTE == 1)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selected SENSEX 1DTE (Priority 4)");
                }
                else
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Default to NIFTY (DTE={niftyDTE})");
                }

                // Now wait for the selected underlying's projected price (max 30 seconds)
                projectedPrice = selectedUnderlying == "NIFTY" ? niftyProjected : sensexProjected;
                double minValidPrice = selectedUnderlying == "NIFTY" ? 1000 : 10000;

                if (projectedPrice < minValidPrice)
                {
                    Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Waiting for {selectedUnderlying} projected price (current={projectedPrice:F0}, min={minValidPrice})...");
                    StatusUpdated?.Invoke($"Waiting for {selectedUnderlying} price data...");

                    int waitedMs = 0;
                    const int maxWaitMs = 30000; // 30 seconds max wait
                    const int pollIntervalMs = 500;

                    while (waitedMs < maxWaitMs)
                    {
                        await Task.Delay(pollIntervalMs);
                        waitedMs += pollIntervalMs;

                        // Re-check the projected price - recalculate from spot price and GIFT NIFTY change
                        double spotPrice = selectedUnderlying == "NIFTY" ? NiftySpotPrice : SensexSpotPrice;
                        if (spotPrice > 0 && GiftNiftyPriorClose > 0 && GiftNiftyPrice > 0)
                        {
                            double changePercent = (GiftNiftyPrice - GiftNiftyPriorClose) / GiftNiftyPriorClose;
                            projectedPrice = spotPrice * (1 + changePercent);
                        }

                        if (projectedPrice >= minValidPrice)
                        {
                            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Got {selectedUnderlying} price={projectedPrice:F0} after {waitedMs}ms");
                            break;
                        }

                        if (waitedMs % 5000 == 0) // Log every 5 seconds
                        {
                            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Still waiting for {selectedUnderlying} price... ({waitedMs / 1000}s)");
                        }
                    }

                    if (projectedPrice < minValidPrice)
                    {
                        Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Timeout waiting for {selectedUnderlying} price after {maxWaitMs / 1000}s - aborting");
                        _optionsAlreadyGenerated = false;
                        StatusUpdated?.Invoke($"Timeout: No {selectedUnderlying} price received");
                        return;
                    }
                }

                // Round projected price to step size for ATM strike
                double atmStrike = Math.Round(projectedPrice / stepSize) * stepSize;

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Final selection - Underlying={selectedUnderlying}, Expiry={selectedExpiry:yyyy-MM-dd}, ATM={atmStrike}, StepSize={stepSize}");
                StatusUpdated?.Invoke($"Selected: {selectedUnderlying} {selectedExpiry:ddMMM} at Strike {atmStrike}");

                // Generate Strikes (ATM +/- 30 = 61 strikes x 2 types = 122 options)
                var generated = new List<MappedInstrument>();
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Generating option symbols (ATM +/- 30)...");

                // Determine if selected expiry is monthly (last expiry of that month)
                var allExpiries = selectedUnderlying == "NIFTY" ? niftyExpiries : sensexExpiries;
                bool isMonthlyExpiry = IsMonthlyExpiry(selectedExpiry, allExpiries);
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Expiry {selectedExpiry:yyyy-MM-dd} is {(isMonthlyExpiry ? "MONTHLY" : "WEEKLY")}");

                // Cache the selected values for TBS Manager and other consumers
                _selectedUnderlying = selectedUnderlying;
                _selectedExpiry = selectedExpiry;
                _selectedIsMonthlyExpiry = isMonthlyExpiry;
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Cached selection - Underlying={selectedUnderlying}, Expiry={selectedExpiry:yyyy-MM-dd}, IsMonthly={isMonthlyExpiry}");

                for (int i = -30; i <= 30; i++)
                {
                    double strike = atmStrike + (i * stepSize);

                    // Create CE
                    var ceOption = CreateOption(selectedUnderlying, selectedExpiry, strike, "CE", stepSize, isMonthlyExpiry);
                    generated.Add(ceOption);

                    // Create PE
                    var peOption = CreateOption(selectedUnderlying, selectedExpiry, strike, "PE", stepSize, isMonthlyExpiry);
                    generated.Add(peOption);
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Generated {generated.Count} option symbols");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Strike range = {atmStrike - (30 * stepSize)} to {atmStrike + (30 * stepSize)}");

                // Generate straddles_config.json from the generated options
                GenerateStraddlesConfig(generated, selectedUnderlying, selectedExpiry);

                // Fire event to notify subscribers
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Invoking OptionsGenerated event...");
                OptionsGenerated?.Invoke(generated);
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Exception occurred - {ex.Message}", ex);
                StatusUpdated?.Invoke("Error generating options: " + ex.Message);
                _optionsAlreadyGenerated = false; // Allow retry on error
            }
        }
        
        /// <summary>
        /// Creates a MappedInstrument with the correct Zerodha symbol format.
        ///
        /// Zerodha Option Symbol Formats:
        /// - Monthly Expiry: UNDERLYING + YY + MMM + STRIKE + TYPE (e.g., NIFTY25DEC24700CE)
        /// - Weekly Expiry (Jan-Sep): UNDERLYING + YY + M + DD + STRIKE + TYPE (e.g., NIFTY25D2924700CE where M=1-9)
        /// - Weekly Expiry (Oct-Dec): UNDERLYING + YY + X + DD + STRIKE + TYPE (e.g., NIFTY25O0324700CE where X=O/N/D)
        /// </summary>
        private MappedInstrument CreateOption(string underlying, DateTime expiry, double strike, string type, int step, bool isMonthlyExpiry)
        {
            string zerodhaSymbol;

            if (isMonthlyExpiry)
            {
                // Monthly format: UNDERLYING + YY + MMM + STRIKE + TYPE
                // Example: NIFTY25DEC24700CE, BANKNIFTY25OCT40000CE
                string monthAbbr = expiry.ToString("MMM").ToUpper();
                zerodhaSymbol = $"{underlying}{expiry:yy}{monthAbbr}{strike:F0}{type}";
            }
            else
            {
                // Weekly format depends on month
                int month = expiry.Month;
                int day = expiry.Day;
                int year = expiry.Year % 100; // 2-digit year

                string monthIndicator;
                if (month >= 1 && month <= 9)
                {
                    // Jan-Sep: Use single digit 1-9
                    // Example: NIFTY25D2924700CE (D=12=December? NO, this is wrong)
                    // Actually: NIFTY + 25 + 1 (for Jan) + 29 (day) + strike + type
                    // Wait, let me re-read: BANKNIFTY+23+9+20+40000+CE means Sep 20
                    monthIndicator = month.ToString();
                }
                else
                {
                    // Oct-Dec: Use O, N, D respectively
                    // Month 10=O, 11=N, 12=D
                    monthIndicator = month switch
                    {
                        10 => "O",
                        11 => "N",
                        12 => "D",
                        _ => month.ToString() // Fallback
                    };
                }

                // Weekly format: UNDERLYING + YY + M + DD + STRIKE + TYPE
                // Day is always 2 digits (padded with 0 if needed)
                zerodhaSymbol = $"{underlying}{year}{monthIndicator}{day:D2}{strike:F0}{type}";
            }

            // NT symbol uses the Zerodha symbol directly
            string ntSymbol = zerodhaSymbol;

            Logger.Debug($"[MarketAnalyzerLogic] CreateOption(): Created {ntSymbol} - {underlying} {expiry:yyyy-MM-dd} {strike} {type} (Monthly={isMonthlyExpiry})");

            return new MappedInstrument
            {
                symbol = ntSymbol,
                zerodhaSymbol = zerodhaSymbol,
                underlying = underlying,
                expiry = expiry,
                strike = strike,
                option_type = type,
                segment = underlying == "SENSEX" ? "BFO-OPT" : "NFO-OPT",
                tick_size = 0.05,
                lot_size = Helpers.SymbolHelper.GetLotSize(underlying), // Get from cache or defaults
                instrument_token = 0 // Will be looked up by SubscriptionManager
            };
        }

        /// <summary>
        /// Determines if an expiry date is a monthly expiry (last expiry of that month).
        /// Monthly expiries are the last Thursday of the month (or last trading day before that).
        /// We check if there are any other expiries in the same month after this date.
        /// </summary>
        private bool IsMonthlyExpiry(DateTime expiry, List<DateTime> allExpiries)
        {
            // Get all expiries in the same month as the target expiry
            var sameMonthExpiries = allExpiries
                .Where(e => e.Year == expiry.Year && e.Month == expiry.Month)
                .OrderBy(e => e)
                .ToList();

            if (sameMonthExpiries.Count == 0)
            {
                // No expiries found - assume monthly (shouldn't happen)
                Logger.Warn($"[MarketAnalyzerLogic] IsMonthlyExpiry: No expiries found for {expiry:yyyy-MM}, assuming monthly");
                return true;
            }

            // The expiry is monthly if it's the LAST one in its month
            DateTime lastExpiry = sameMonthExpiries.Last();
            bool isMonthly = expiry.Date == lastExpiry.Date;

            Logger.Debug($"[MarketAnalyzerLogic] IsMonthlyExpiry: {expiry:yyyy-MM-dd} - Month has {sameMonthExpiries.Count} expiries, last is {lastExpiry:yyyy-MM-dd}, isMonthly={isMonthly}");

            return isMonthly;
        }

        private DateTime GetNearestExpiry(List<DateTime> expiries)
        {
            var nearest = expiries.Where(e => e.Date >= DateTime.Today).OrderBy(e => e).FirstOrDefault();
            Logger.Debug($"[MarketAnalyzerLogic] GetNearestExpiry(): Found {nearest:yyyy-MM-dd} from {expiries.Count} expiries");
            return nearest;
        }

        private bool IsSameDay(DateTime d1, DateTime d2)
        {
            return d1.Date == d2.Date;
        }

        /// <summary>
        /// Generates straddles_config.json from the generated options
        /// Creates straddle definitions for each strike (CE + PE pair)
        /// </summary>
        private void GenerateStraddlesConfig(List<MappedInstrument> options, string underlying, DateTime expiry)
        {
            try
            {
                Logger.Info($"[MarketAnalyzerLogic] GenerateStraddlesConfig(): Creating straddles for {underlying} expiry {expiry:yyyy-MM-dd}");

                // Group options by strike
                var strikeGroups = options
                    .Where(o => o.strike.HasValue)
                    .GroupBy(o => o.strike.Value)
                    .OrderBy(g => g.Key);

                var straddles = new List<object>();

                foreach (var group in strikeGroups)
                {
                    var ce = group.FirstOrDefault(o => o.option_type == "CE");
                    var pe = group.FirstOrDefault(o => o.option_type == "PE");

                    if (ce != null && pe != null)
                    {
                        // Create synthetic straddle symbol: NIFTY25DEC23400_STRDL
                        string monthAbbr = expiry.ToString("MMM").ToUpper();
                        string syntheticSymbol = $"{underlying}{expiry:yy}{monthAbbr}{group.Key:F0}_STRDL";

                        straddles.Add(new
                        {
                            SyntheticSymbolNinjaTrader = syntheticSymbol,
                            CESymbol = ce.symbol,
                            PESymbol = pe.symbol
                        });
                    }
                }

                // Write to straddles_config.json (synchronized to prevent concurrent access errors)
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string configPath = Path.Combine(documentsPath, "NinjaTrader 8", "ZerodhaAdapter", "straddles_config.json");

                lock (_straddlesConfigLock)
                {
                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(configPath);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var config = new { Straddles = straddles };
                    string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                    File.WriteAllText(configPath, json);

                    Logger.Info($"[MarketAnalyzerLogic] GenerateStraddlesConfig(): Written {straddles.Count} straddles to {configPath}");
                }

                // Reload straddle configurations in the service (outside lock to avoid deadlock)
                try
                {
                    var adapter = Connector.Instance.GetAdapter() as ZerodhaAdapter;
                    adapter?.SyntheticStraddleService?.ReloadConfigurations();
                    Logger.Info("[MarketAnalyzerLogic] GenerateStraddlesConfig(): Triggered straddle config reload");
                }
                catch (Exception reloadEx)
                {
                    Logger.Warn($"[MarketAnalyzerLogic] GenerateStraddlesConfig(): Could not reload straddle config - {reloadEx.Message}");
                }

                // Create NinjaTrader instruments for synthetic straddles
                CreateSyntheticStraddleInstruments(strikeGroups, underlying, expiry);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] GenerateStraddlesConfig(): Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates NinjaTrader instruments for synthetic straddle symbols
        /// </summary>
        private void CreateSyntheticStraddleInstruments(IOrderedEnumerable<IGrouping<double, MappedInstrument>> strikeGroups, string underlying, DateTime expiry)
        {
            try
            {
                Logger.Info($"[MarketAnalyzerLogic] CreateSyntheticStraddleInstruments(): Creating NT instruments for {underlying} straddles");
                int created = 0;

                foreach (var group in strikeGroups)
                {
                    var ce = group.FirstOrDefault(o => o.option_type == "CE");
                    var pe = group.FirstOrDefault(o => o.option_type == "PE");

                    if (ce != null && pe != null)
                    {
                        string monthAbbr = expiry.ToString("MMM").ToUpper();
                        string straddleSymbol = $"{underlying}{expiry:yy}{monthAbbr}{group.Key:F0}_STRDL";

                        // Create instrument definition for the synthetic straddle
                        var instrumentDef = new InstrumentDefinition
                        {
                            Symbol = straddleSymbol,
                            BrokerSymbol = straddleSymbol,
                            Segment = underlying == "SENSEX" ? "BSE" : "NSE",
                            MarketType = MarketType.UsdM // Synthetic options use UsdM
                        };

                        // Create instrument on UI thread
                        NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                bool success = InstrumentManager.Instance.CreateInstrument(instrumentDef, out string ntName);
                                if (success)
                                {
                                    Logger.Debug($"[MarketAnalyzerLogic] CreateSyntheticStraddleInstruments(): Created NT instrument {ntName}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[MarketAnalyzerLogic] CreateSyntheticStraddleInstruments(): Error creating {straddleSymbol} - {ex.Message}");
                            }
                        });

                        created++;
                    }
                }

                Logger.Info($"[MarketAnalyzerLogic] CreateSyntheticStraddleInstruments(): Queued {created} synthetic straddle instruments for creation");

                // Create dynamic instrument list after a delay to allow instruments to be created
                Task.Delay(2000).ContinueWith(_ =>
                {
                    NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                    {
                        CreateDynamicInstrumentList(strikeGroups, underlying, expiry);
                    });
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] CreateSyntheticStraddleInstruments(): Exception - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates a dynamic NinjaTrader InstrumentList containing all generated straddle instruments.
        /// List naming: SX_DDMMMYY for SENSEX, NF_DDMMMYY for NIFTY
        /// </summary>
        private void CreateDynamicInstrumentList(IOrderedEnumerable<IGrouping<double, MappedInstrument>> strikeGroups, string underlying, DateTime expiry)
        {
            try
            {
                // Create list name: SX_24DEC24 for SENSEX, NF_24DEC24 for NIFTY
                string prefix = underlying == "SENSEX" ? "SX" : "NF";
                string listName = $"{prefix}_{expiry:ddMMMyy}".ToUpper();

                Logger.Info($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Creating instrument list '{listName}'");

                // Check if list already exists and remove it
                var existingList = InstrumentList.All.FirstOrDefault(l => l.Name == listName);
                if (existingList != null)
                {
                    existingList.Instruments.Clear();
                    Logger.Info($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Cleared existing list '{listName}'");
                }
                else
                {
                    // Create new list
                    existingList = new InstrumentList { Name = listName };
                    Logger.Info($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Created new list '{listName}'");
                }

                int addedCount = 0;

                // Add straddle instruments to the list
                foreach (var group in strikeGroups)
                {
                    var ce = group.FirstOrDefault(o => o.option_type == "CE");
                    var pe = group.FirstOrDefault(o => o.option_type == "PE");

                    if (ce != null && pe != null)
                    {
                        string monthAbbr = expiry.ToString("MMM").ToUpper();
                        string straddleSymbol = $"{underlying}{expiry:yy}{monthAbbr}{group.Key:F0}_STRDL";

                        // Find the instrument in NinjaTrader's instrument database
                        var instrument = Instrument.All.FirstOrDefault(i =>
                            i.FullName.Equals(straddleSymbol, StringComparison.OrdinalIgnoreCase) ||
                            i.MasterInstrument.Name.Equals(straddleSymbol, StringComparison.OrdinalIgnoreCase));

                        if (instrument != null)
                        {
                            if (!existingList.Instruments.Contains(instrument))
                            {
                                existingList.Instruments.Add(instrument);
                                addedCount++;
                                Logger.Debug($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Added {straddleSymbol} to list");
                            }
                        }
                        else
                        {
                            Logger.Debug($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Instrument '{straddleSymbol}' not found in database");
                        }
                    }
                }

                Logger.Info($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Added {addedCount} instruments to list '{listName}'");

                // Store reference for use by Market Analyzer / other components
                // Note: Programmatically created InstrumentLists may not appear in Control Center GUI
                // but can be accessed via InstrumentList.All for use in scripts
                _currentInstrumentList = existingList;
                Logger.Info($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): List '{listName}' ready for use");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] CreateDynamicInstrumentList(): Exception - {ex.Message}", ex);
            }
        }
    }
}
