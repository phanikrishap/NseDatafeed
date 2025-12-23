using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using QANinjaAdapter.Models;
using QANinjaAdapter.Services.Instruments;

using MappedInstrument = QANinjaAdapter.Models.MappedInstrument;

namespace QANinjaAdapter.Services.Analysis
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

        // Track if we've already generated options to avoid duplicates
        private bool _optionsAlreadyGenerated = false;

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
                Logger.Info($"[MarketAnalyzerLogic] UpdatePrice(): GIFT_NIFTY updated - Price={GiftNiftyPrice}, PriorClose={GiftNiftyPriorClose}");

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
                Logger.Info($"[MarketAnalyzerLogic] UpdatePrice(): NIFTY_SPOT updated - Price={NiftySpotPrice}");

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
                Logger.Info($"[MarketAnalyzerLogic] UpdatePrice(): SENSEX_SPOT updated - Price={SensexSpotPrice}");

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
                Logger.Info($"[MarketAnalyzerLogic] CheckAndCalculate(): Change% = ({GiftNiftyPrice} - {GiftNiftyPriorClose}) / {GiftNiftyPriorClose} = {changePercent:P4}");
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

            Logger.Info($"[MarketAnalyzerLogic] CheckAndCalculate(): Projected Opens - NIFTY={niftyProjected:F2}, SENSEX={sensexProjected:F2}");
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

                for (int i = -30; i <= 30; i++)
                {
                    double strike = atmStrike + (i * stepSize);

                    // Create CE
                    var ceOption = CreateOption(selectedUnderlying, selectedExpiry, strike, "CE", stepSize);
                    generated.Add(ceOption);

                    // Create PE
                    var peOption = CreateOption(selectedUnderlying, selectedExpiry, strike, "PE", stepSize);
                    generated.Add(peOption);
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Generated {generated.Count} option symbols");
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Strike range = {atmStrike - (30 * stepSize)} to {atmStrike + (30 * stepSize)}");

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
        
        private MappedInstrument CreateOption(string underlying, DateTime expiry, double strike, string type, int step)
        {
            // Create Zerodha-style symbol: NIFTY2412524500CE (NIFTY + YYMMMDD + STRIKE + TYPE)
            // Format: UNDERLYING + YY + MMM (3-letter month) + DD + STRIKE + CE/PE
            string monthAbbr = expiry.ToString("MMM").ToUpper();
            string zerodhaSymbol = $"{underlying}{expiry:yy}{monthAbbr}{expiry:dd}{strike:F0}{type}";

            // NT symbol can be the same or slightly different
            string ntSymbol = zerodhaSymbol;

            Logger.Debug($"[MarketAnalyzerLogic] CreateOption(): Created {ntSymbol} - {underlying} {expiry:yyyy-MM-dd} {strike} {type}");

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
                lot_size = underlying == "SENSEX" ? 10 : 25, // SENSEX lot = 10, NIFTY lot = 25
                instrument_token = 0 // Will be looked up by SubscriptionManager
            };
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
    }
}
