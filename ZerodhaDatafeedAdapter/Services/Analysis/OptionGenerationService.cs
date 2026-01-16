using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Service for generating option symbols based on underlying price and expiry.
    /// Handles Zerodha-specific naming conventions for weekly and monthly expiries.
    /// </summary>
    /// <summary>
    /// Event args for ATM strike changes.
    /// </summary>
    public class ATMChangedEventArgs : EventArgs
    {
        public string Underlying { get; set; }
        public decimal OldATM { get; set; }
        public decimal NewATM { get; set; }
        public decimal StrikeStep { get; set; }
    }

    public class OptionGenerationService
    {
        private static readonly Lazy<OptionGenerationService> _instance = new Lazy<OptionGenerationService>(() => new OptionGenerationService());
        public static OptionGenerationService Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, decimal> _atmStrikes = new ConcurrentDictionary<string, decimal>();

        /// <summary>
        /// Fired when ATM strike changes by one or more strike steps.
        /// Used by Option Signals to re-center the strike grid.
        /// </summary>
        public event EventHandler<ATMChangedEventArgs> ATMChanged;

        private OptionGenerationService() { }

        public void SetATMStrike(string underlying, decimal strike)
        {
            decimal oldAtm = _atmStrikes.TryGetValue(underlying, out var existing) ? existing : 0;
            _atmStrikes[underlying] = strike;

            // Fire event if ATM changed by at least one strike step
            if (oldAtm > 0 && strike != oldAtm)
            {
                decimal strikeStep = (decimal)GetStrikeStep(underlying);
                decimal atmDiff = Math.Abs(strike - oldAtm);

                // Only fire if change is >= 1 strike step (significant shift)
                if (atmDiff >= strikeStep)
                {
                    ATMChanged?.Invoke(this, new ATMChangedEventArgs
                    {
                        Underlying = underlying,
                        OldATM = oldAtm,
                        NewATM = strike,
                        StrikeStep = strikeStep
                    });
                }
            }
        }

        public decimal GetATMStrike(string underlying) => _atmStrikes.TryGetValue(underlying, out var strike) ? strike : 0;

        public async Task<List<MappedInstrument>> GenerateOptionsAsync(string underlying, double currentPrice, DateTime expiry, int strikeCount = 10)
        {
            Logger.Info($"[OGS] Generating options for {underlying} @ {currentPrice}, Expiry: {expiry:dd-MMM-yyyy}");
            
            var options = new List<MappedInstrument>();
            int lotSize = InstrumentManager.Instance.GetLotSizeForUnderlying(underlying);
            if (lotSize == 0) lotSize = 50; // Fallback

            double strikeStep = GetStrikeStep(underlying);
            double atmStrike = Math.Round(currentPrice / strikeStep) * strikeStep;

            string segment = (underlying == "SENSEX" || underlying == "BANKEX") ? "BFO-OPT" : "NFO-OPT";

            for (int i = -strikeCount; i <= strikeCount; i++)
            {
                double strike = atmStrike + (i * strikeStep);
                
                // Generate CE and PE
                foreach (var type in new[] { "CE", "PE" })
                {
                    var (token, symbol) = InstrumentManager.Instance.LookupOptionDetails(segment, underlying, expiry.ToString("yyyy-MM-dd"), strike, type);
                    if (token > 0)
                    {
                        options.Add(new MappedInstrument
                        {
                            symbol = symbol,
                            instrument_token = token,
                            underlying = underlying,
                            expiry = expiry,
                            strike = strike,
                            option_type = type,
                            lot_size = lotSize,
                            exchange = segment.Split('-')[0]
                        });
                    }
                }
            }

            Logger.Info($"[OGS] Generated {options.Count} options for {underlying}");
            return options;
        }

        public class OptionSelectionResult
        {
            public string Underlying { get; set; }
            public DateTime Expiry { get; set; }
            public bool IsMonthlyExpiry { get; set; }
            public double ProjectedPrice { get; set; }
            public int StepSize { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Selects the best underlying and expiry based on DTE priority:
        /// 1. NIFTY 0DTE, 2. SENSEX 0DTE, 3. NIFTY 1DTE, 4. SENSEX 1DTE, 5. Default NIFTY
        /// </summary>
        public async Task<OptionSelectionResult> SelectBestOptionConfigurationAsync(double niftyProjected, double sensexProjected)
        {
            try
            {
                // Fetch expiries
                var niftyExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("NIFTY");
                var sensexExpiries = await InstrumentManager.Instance.GetExpiriesForUnderlyingAsync("SENSEX");

                var niftyNear = GetNearestExpiry(niftyExpiries);
                var sensexNear = GetNearestExpiry(sensexExpiries);

                // Calculate DTE
                double niftyDTE = (niftyNear.Date - DateTime.Today).TotalDays;
                double sensexDTE = (sensexNear.Date - DateTime.Today).TotalDays;

                string selectedUnderlying;
                DateTime selectedExpiry;
                int stepSize;
                double projectedPrice;
                string message;

                if (niftyDTE == 0)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    message = "Selected NIFTY 0DTE (Priority 1)";
                }
                else if (sensexDTE == 0)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    projectedPrice = sensexProjected;
                    message = "Selected SENSEX 0DTE (Priority 2)";
                }
                else if (niftyDTE == 1)
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    message = "Selected NIFTY 1DTE (Priority 3)";
                }
                else if (sensexDTE == 1)
                {
                    selectedUnderlying = "SENSEX";
                    selectedExpiry = sensexNear;
                    stepSize = 100;
                    projectedPrice = sensexProjected;
                    message = "Selected SENSEX 1DTE (Priority 4)";
                }
                else
                {
                    selectedUnderlying = "NIFTY";
                    selectedExpiry = niftyNear;
                    stepSize = 50;
                    projectedPrice = niftyProjected;
                    message = $"Default to NIFTY (DTE={niftyDTE})";
                }

                bool isMonthly = SymbolHelper.IsMonthlyExpiry(selectedExpiry, selectedUnderlying == "NIFTY" ? niftyExpiries : sensexExpiries);

                return new OptionSelectionResult
                {
                    Underlying = selectedUnderlying,
                    Expiry = selectedExpiry,
                    IsMonthlyExpiry = isMonthly,
                    ProjectedPrice = projectedPrice,
                    StepSize = stepSize,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"[OGS] SelectBestOptionConfigurationAsync error: {ex.Message}");
                return null;
            }
        }

        private DateTime GetNearestExpiry(List<DateTime> expiries)
        {
            if (expiries == null || expiries.Count == 0) return DateTime.Today.AddDays(7); // Safe fallback
            var today = DateTime.Today;
            // Return first expiry >= today
            return expiries.Where(e => e.Date >= today).OrderBy(e => e).FirstOrDefault();
        }

        public double GetStrikeStep(string underlying)
        {
            if (underlying == "NIFTY") return 50;
            if (underlying == "BANKNIFTY") return 100;
            if (underlying == "SENSEX") return 100;
            if (underlying == "FINNIFTY") return 50;
            if (underlying == "MIDCPNIFTY") return 25;
            return 100;
        }

        public List<string> GetCachedExpiries(string underlying)
        {
            var expiries = InstrumentManager.Instance.GetExpiriesForUnderlying(underlying);
            return expiries.Select(e => e.ToString("dd-MMM-yyyy")).ToList();
        }

        public int GetLotSize(string underlying)
        {
            return InstrumentManager.Instance.GetLotSizeForUnderlying(underlying);
        }
    }
}
