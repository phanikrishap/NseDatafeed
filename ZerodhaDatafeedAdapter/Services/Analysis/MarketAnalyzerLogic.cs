using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Hedge;
using ZerodhaDatafeedAdapter.Services.Instruments;
using ZerodhaDatafeedAdapter.Services.Telegram;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Core coordinator for Market Analyzer logic.
    /// Manages the ticker list and orchestrates price updates and option generation.
    /// Calculates projected opens based on GIFT NIFTY change and auto-generates options.
    /// </summary>
    public class MarketAnalyzerLogic
    {
        private static readonly Lazy<MarketAnalyzerLogic> _instance = new Lazy<MarketAnalyzerLogic>(() => new MarketAnalyzerLogic());
        public static MarketAnalyzerLogic Instance => _instance.Value;

        // Static ticker instances - delegated to TickerService
        public TickerData GiftNiftyTicker => TickerService.Instance.GiftNiftyTicker;
        public TickerData NiftyTicker => TickerService.Instance.NiftyTicker;
        public TickerData SensexTicker => TickerService.Instance.SensexTicker;
        public TickerData NiftyFuturesTicker => TickerService.Instance.NiftyFuturesTicker;

        // Observable collection for UI binding
        public ObservableCollection<TickerData> Tickers => TickerService.Instance.Tickers;

        // Spot prices - delegated to TickerService
        public double GiftNiftyPrice => TickerService.Instance.GiftNiftyPrice;
        public double NiftySpotPrice => TickerService.Instance.NiftySpotPrice;
        public double SensexSpotPrice => TickerService.Instance.SensexSpotPrice;
        public double NiftyFuturesPrice => TickerService.Instance.NiftyFuturesPrice;

        // Prior close for GIFT NIFTY
        public double GiftNiftyPriorClose => TickerService.Instance.GiftNiftyPriorClose;

        // Projected open prices - delegated/proxied or managed via subscription
        public double ProjectedNiftyOpenPrice { get; private set; }
        public double ProjectedSensexOpenPrice { get; private set; }

        // NIFTY Futures contract info - delegated to TickerService
        public string NiftyFuturesSymbol => TickerService.Instance.NiftyFuturesSymbol;
        public long NiftyFuturesToken => TickerService.Instance.NiftyFuturesToken;
        public DateTime NiftyFuturesExpiry => TickerService.Instance.NiftyFuturesExpiry;

        // System.Reactive - Project Open Update Subject (Proxied from Service)
        public IObservable<ProjectedOpenUpdate> ProjectedOpenStream => ProjectedOpenService.Instance.ProjectedOpenStream;

        // Observable streams for UI binding
        public IObservable<TickerPriceUpdate> PriceUpdateStream => TickerService.Instance.PriceUpdateStream;

        // Selection State
        public string SelectedExpiry { get; set; }
        public string SelectedUnderlying { get; set; }
        public bool SelectedIsMonthlyExpiry { get; set; }

        // Events
        public event Action<string> StatusUpdated;
        public event Action<string> TickerUpdated // Facade for TickerService event
        {
            add => TickerService.Instance.TickerUpdated += value;
            remove => TickerService.Instance.TickerUpdated -= value;
        }

        public event Action<string, string> HistoricalDataStatusChanged;
        public event Action<List<MappedInstrument>> OptionsGenerated;
        public event Action PriceSyncReady;
        public event Action<string, double> OptionPriceUpdated;
        public event Action<string, double> PriceUpdated;

        private bool _priceSyncFired = false;
        /// <summary>
        /// Returns true if price sync is ready (NIFTY and SENSEX prices received).
        /// Used by late subscribers to check if they missed the PriceSyncReady event.
        /// </summary>
        public bool IsPriceSyncReady => _priceSyncFired;
        private int _optionsAlreadyGenerated = 0; // 0=false, 1=true - use Interlocked for thread-safe check-and-set
        private readonly object _syncLock = new object();

        // Internal price cache to replace legacy MarketDataHub
        private readonly ConcurrentDictionary<string, double> _priceCache = new ConcurrentDictionary<string, double>();

        private MarketAnalyzerLogic()
        {
            Logger.Info("[MarketAnalyzerLogic] Constructor: Initializing singleton instance");

            // Subscribe to Projected Open updates to trigger option generation
            ProjectedOpenService.Instance.ProjectedOpenStream.Subscribe(update =>
            {
                ProjectedNiftyOpenPrice = update.NiftyProjectedOpen;
                ProjectedSensexOpenPrice = update.SensexProjectedOpen;
                
                Logger.Info($"[MarketAnalyzerLogic] Received Projected Open Update: NIFTY={ProjectedNiftyOpenPrice:F0}, SENSEX={ProjectedSensexOpenPrice:F0}");
                
                // Trigger option generation logic
                TriggerOptionsGeneration(ProjectedNiftyOpenPrice, ProjectedSensexOpenPrice);
            });
        }

        public void NotifyHistoricalDataStatus(string symbol, string status) => HistoricalDataStatusChanged?.Invoke(symbol, status);

        public void UpdateOptionPrice(string symbol, decimal price, DateTime timestamp)
        {
            _priceCache[symbol] = (double)price;
            CheckAndFirePriceSyncReady();
        }

        public void UpdatePrice(string symbol, double price)
        {
            UpdatePrice(symbol, price, 0);
        }

        public void UpdatePrice(string symbol, double price, double priorClose)
        {
            // Delegate to TickerService
            TickerService.Instance.UpdatePrice(symbol, price, priorClose);

            // Also store price in internal cache (for option chain and other lookups)
            _priceCache[symbol] = price;

            CheckAndFirePriceSyncReady();
            // No longer calling CheckAndCalculate() directly here.
            // ProjectedOpenService will react to TickerService updates and publish projected opens.
        }

        public void UpdatePrice(string symbol, double price, DateTime timestamp)
        {
            UpdatePrice(symbol, price, 0);
        }

        /// <summary>
        /// Updates the Close (prior day close) for a symbol - does NOT affect CurrentPrice
        /// </summary>
        public void UpdateClose(string symbol, double closePrice)
        {
            TickerService.Instance.UpdateClose(symbol, closePrice);
            
            // CheckAndCalculate is triggered by TickerService updates via PriceUpdateStream subscription usually,
            // but here we might want to check explicitly if prior close changed, as it affects NetChange%
            if (TickerService.Instance.IsGiftNifty(symbol))
            {
               ProjectedOpenService.Instance.CalculateLiveProjectedOpens();
            }
        }

        // Cache Facades
        public List<string> GetCachedExpiries(string underlying) => OptionGenerationService.Instance.GetCachedExpiries(underlying);
        public int GetLotSize(string underlying) => OptionGenerationService.Instance.GetLotSize(underlying);
        public int GetCachedLotSize(string underlying) => OptionGenerationService.Instance.GetLotSize(underlying);

        public async Task GenerateOptionsAsync(string underlying, double price, DateTime expiry)
        {
            var options = await OptionGenerationService.Instance.GenerateOptionsAsync(underlying, price, expiry);
            OptionsGenerated?.Invoke(options);
        }

        public void SetATMStrike(string underlying, decimal strike) => OptionGenerationService.Instance.SetATMStrike(underlying, strike);
        public decimal GetATMStrike(string underlying) => OptionGenerationService.Instance.GetATMStrike(underlying);

        public double GetPrice(string symbol)
        {
            return _priceCache.TryGetValue(symbol, out double price) ? price : 0;
        }

        /// <summary>
        /// Checks if the market is currently open for trading.
        /// Wrapper around DateTimeHelper.IsMarketOpen for convenience.
        /// </summary>
        public bool IsMarketOpen() => Helpers.DateTimeHelper.IsMarketOpen();

        /// <summary>
        /// Gets the projected open price for the specified underlying.
        /// </summary>
        public double GetProjectedOpen(string underlying)
        {
            if (string.IsNullOrEmpty(underlying)) return 0;
            if (underlying.Equals("NIFTY", StringComparison.OrdinalIgnoreCase))
                return ProjectedNiftyOpenPrice;
            if (underlying.Equals("SENSEX", StringComparison.OrdinalIgnoreCase))
                return ProjectedSensexOpenPrice;
            return 0;
        }

        public void Reset()
        {
            _priceCache.Clear();
            _priceSyncFired = false;
            System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);

            // Reset local projected state
            ProjectedNiftyOpenPrice = 0;
            ProjectedSensexOpenPrice = 0;

            // Delegate to TickerService to reset tickers
            TickerService.Instance.Reset();
            
            // Re-subscribe to ProjectedOpenService if needed? No, subscription is permanent in constructor.
            // Resetting ProjectedOpenService handles the logic.
            ProjectedOpenService.Instance.Reset();
        }

        /// <summary>
        /// Fires PriceSyncReady after options are generated. This is the definitive signal that
        /// Option Chain has all the data TBS needs: underlying, expiry, DTE, ATM strikes, and prices.
        /// </summary>
        private void FirePriceSyncReadyAfterOptionsGenerated(string underlying, DateTime expiry, int dte, double atmStrike)
        {
            lock (_syncLock)
            {
                if (_priceSyncFired) return;
                _priceSyncFired = true;

                Logger.Info($"[MarketAnalyzerLogic] PriceSyncReady fired after OptionsGenerated: Underlying={underlying}, Expiry={expiry:dd-MMM-yyyy}, DTE={dte}, ATM={atmStrike}");

                // Publish to reactive hub (primary - uses ReplaySubject for late subscribers)
                MarketDataReactiveHub.Instance.PublishPriceSyncReady();

                // Fire legacy event for backward compatibility
                PriceSyncReady?.Invoke();

                // Send Telegram alert for Option Chain generated
                TelegramAlertService.Instance.SendOptionChainAlert(underlying, dte, atmStrike);
            }
        }

        private void CheckAndFirePriceSyncReady()
        {
             // Deprecated but called by UpdateOptionPrice
             // Keeping for compatibility
        }



        /// <summary>
        /// Generates option symbols based on DTE priority:
        /// 1. NIFTY 0DTE, 2. SENSEX 0DTE, 3. NIFTY 1DTE, 4. SENSEX 1DTE, 5. Default NIFTY
        /// Awaits InstrumentDbReadyStream before performing database lookups.
        /// </summary>
        /// <summary>
        /// Generates option symbols based on DTE priority using OptionGenerationService.
        /// Awaits InstrumentDbReadyStream before performing database lookups.
        /// </summary>
        private async void GenerateOptions(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Starting with niftyProjected={niftyProjected:F2}, sensexProjected={sensexProjected:F2}");

            try
            {
                // Flag already set by Interlocked.CompareExchange in CheckAndCalculate()
                // No need to set again here

                // Await instrument database ready signal before performing lookups
                try
                {
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Awaiting InstrumentDbReady signal...");
                    var dbReady = await MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                        .Timeout(TimeSpan.FromSeconds(60))
                        .FirstAsync()
                        .ToTask();

                    if (!dbReady)
                    {
                        Logger.Error("[MarketAnalyzerLogic] GenerateOptions(): Instrument DB not ready - cannot generate options");
                        System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                        StatusUpdated?.Invoke("Error: Instrument database not ready");
                        return;
                    }
                    Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): InstrumentDbReady signal received, proceeding with option generation");
                }
                catch (TimeoutException)
                {
                    Logger.Error("[MarketAnalyzerLogic] GenerateOptions(): Timeout waiting for InstrumentDbReady (60s)");
                    System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                    StatusUpdated?.Invoke("Error: Timeout waiting for instrument database");
                    return;
                }

                // 1. Select Best Configuration via Service
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Selecting best option configuration...");
                var selection = await OptionGenerationService.Instance.SelectBestOptionConfigurationAsync(niftyProjected, sensexProjected);

                if (selection == null)
                {
                    Logger.Error("[MarketAnalyzerLogic] GenerateOptions(): Failed to select option configuration");
                    System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                    StatusUpdated?.Invoke("Error: Failed to select options");
                    return;
                }

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): {selection.Message}");
                
                // Store selection in state
                SelectedUnderlying = selection.Underlying;
                SelectedExpiry = selection.Expiry.ToString("dd-MMM-yyyy");
                SelectedIsMonthlyExpiry = selection.IsMonthlyExpiry;

                // 2. Wait for price if needed
                double projectedPrice = selection.ProjectedPrice;
                double minValidPrice = selection.Underlying == "NIFTY" ? 1000 : 10000;

                if (projectedPrice < minValidPrice)
                {
                    Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Waiting for {selection.Underlying} projected price (current={projectedPrice:F0}, min={minValidPrice})...");
                    StatusUpdated?.Invoke($"Waiting for {selection.Underlying} price data...");

                    int waitedMs = 0;
                    const int maxWaitMs = 30000;
                    const int pollIntervalMs = 500;

                    while (waitedMs < maxWaitMs)
                    {
                        await Task.Delay(pollIntervalMs);
                        waitedMs += pollIntervalMs;

                        // Re-calculate based on live data
                        double spotPrice = selection.Underlying == "NIFTY" ? NiftySpotPrice : SensexSpotPrice;
                        if (spotPrice > 0 && GiftNiftyPriorClose > 0 && GiftNiftyPrice > 0)
                        {
                            double changePercent = (GiftNiftyPrice - GiftNiftyPriorClose) / GiftNiftyPriorClose;
                            projectedPrice = spotPrice * (1 + changePercent);
                        }
                        else if (selection.Underlying == "NIFTY" && ProjectedNiftyOpenPrice > 0)
                        {
                            projectedPrice = ProjectedNiftyOpenPrice;
                        }
                        else if (selection.Underlying == "SENSEX" && ProjectedSensexOpenPrice > 0)
                        {
                            projectedPrice = ProjectedSensexOpenPrice;
                        }

                        if (projectedPrice >= minValidPrice)
                        {
                            Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Got {selection.Underlying} price={projectedPrice:F0} after {waitedMs}ms");
                            break;
                        }
                    }

                    if (projectedPrice < minValidPrice)
                    {
                        Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Timeout waiting for {selection.Underlying} price - aborting");
                        System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
                        StatusUpdated?.Invoke($"Timeout: No {selection.Underlying} price received");
                        return;
                    }
                }

                double atmStrike = Math.Round(projectedPrice / selection.StepSize) * selection.StepSize;
                
                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Selected {selection.Underlying} {selection.Expiry:yyyy-MM-dd} ATM={atmStrike} (Monthly={selection.IsMonthlyExpiry})");
                StatusUpdated?.Invoke($"Selected: {selection.Underlying} {selection.Expiry:ddMMM} at Strike {atmStrike}");

                // 3. Generate Options via Service
                // Generate options (ATM +/- 30 = 61 strikes x 2 types = 122 options)
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Generating options via service...");
                var generated = await OptionGenerationService.Instance.GenerateOptionsAsync(
                    selection.Underlying, 
                    projectedPrice, 
                    selection.Expiry, 
                    strikeCount: 30);

                Logger.Info($"[MarketAnalyzerLogic] GenerateOptions(): Service returned {generated.Count} option symbols");

                // Publish to reactive hub (primary - OptionChainWindow uses this)
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Publishing to MarketDataReactiveHub...");
                int dte = (int)(selection.Expiry.Date - DateTime.Today).TotalDays;
                
                MarketDataReactiveHub.Instance.PublishOptionsGenerated(
                    generated,
                    selection.Underlying,
                    selection.Expiry,
                    dte,
                    atmStrike,
                    projectedPrice);

                // Fire legacy event for backward compatibility
                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Invoking OptionsGenerated event...");
                OptionsGenerated?.Invoke(generated);

                // Initialize hedge subscriptions (liquid strikes for hedge selection)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HedgeSubscriptionService.Instance.InitializeAsync(
                            selection.Underlying,
                            selection.Expiry,
                            (decimal)atmStrike,
                            selection.IsMonthlyExpiry);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MarketAnalyzerLogic] HedgeSubscriptionService initialization failed: {ex.Message}");
                    }
                });

                // Fire PriceSyncReady AFTER options are generated - this is the definitive signal
                // that Option Chain has underlying, expiry, DTE, ATM strikes, and is ready for TBS
                FirePriceSyncReadyAfterOptionsGenerated(selection.Underlying, selection.Expiry, dte, atmStrike);

                Logger.Info("[MarketAnalyzerLogic] GenerateOptions(): Completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MarketAnalyzerLogic] GenerateOptions(): Exception - {ex.Message}", ex);
                StatusUpdated?.Invoke("Error generating options: " + ex.Message);
                System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
            }
        }

        private bool IsMonthlyExpiry(DateTime expiry, List<DateTime> allExpiries)
        {
            var sameMonthExpiries = allExpiries
                .Where(e => e.Year == expiry.Year && e.Month == expiry.Month)
                .OrderBy(e => e)
                .ToList();

            if (sameMonthExpiries.Count == 0) return true;

            DateTime lastExpiry = sameMonthExpiries.Last();
            return expiry.Date == lastExpiry.Date;
        }

        /// <summary>
        /// Resets the option generation flag to allow re-generation
        /// </summary>
        public void ResetOptionsGeneration()
        {
            System.Threading.Interlocked.Exchange(ref _optionsAlreadyGenerated, 0);
            Logger.Info("[MarketAnalyzerLogic] ResetOptionsGeneration(): Options can now be regenerated");
        }

        /// <summary>
        /// Triggers option generation from external source (e.g., MarketDataReactiveHub).
        /// Called when projected opens are ready via reactive stream.
        /// </summary>
        public void TriggerOptionsGeneration(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] TriggerOptionsGeneration(): Called with NIFTY={niftyProjected:F0}, SENSEX={sensexProjected:F0}");

            // Store projected values
            ProjectedNiftyOpenPrice = niftyProjected;
            ProjectedSensexOpenPrice = sensexProjected;

            // Update ticker projected values
            if (NiftyTicker != null) NiftyTicker.ProjectedOpen = niftyProjected;
            if (SensexTicker != null) SensexTicker.ProjectedOpen = sensexProjected;

            // Use Interlocked to prevent duplicate generation
            if (System.Threading.Interlocked.CompareExchange(ref _optionsAlreadyGenerated, 1, 0) == 0)
            {
                Logger.Info("[MarketAnalyzerLogic] TriggerOptionsGeneration(): Starting option generation...");
                GenerateOptionsAndPublishToHub(niftyProjected, sensexProjected);
            }
            else
            {
                Logger.Debug("[MarketAnalyzerLogic] TriggerOptionsGeneration(): Options already generated, skipping");
            }
        }

        /// <summary>
        /// Generates options and publishes to MarketDataReactiveHub.
        /// Awaits InstrumentDbReadyStream before performing database lookups.
        /// </summary>
        private void GenerateOptionsAndPublishToHub(double niftyProjected, double sensexProjected)
        {
            Logger.Info($"[MarketAnalyzerLogic] GenerateOptionsAndPublishToHub(): Redirecting to GenerateOptions with niftyProjected={niftyProjected:F2}, sensexProjected={sensexProjected:F2}");
            GenerateOptions(niftyProjected, sensexProjected);
        }
    }
}


