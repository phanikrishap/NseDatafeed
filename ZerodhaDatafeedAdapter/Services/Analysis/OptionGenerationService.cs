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

        private double GetStrikeStep(string underlying)
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
