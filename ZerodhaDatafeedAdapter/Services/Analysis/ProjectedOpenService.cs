using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Service responsible for calculating and streaming Projected Open prices 
    /// based on GIFT NIFTY movements and Index Prior Closes.
    /// </summary>
    public class ProjectedOpenService
    {
        private static readonly Lazy<ProjectedOpenService> _instance = new Lazy<ProjectedOpenService>(() => new ProjectedOpenService());
        public static ProjectedOpenService Instance => _instance.Value;

        // Projected opens
        public double ProjectedNiftyOpen { get; private set; }
        public double ProjectedSensexOpen { get; private set; }

        private readonly Subject<ProjectedOpenUpdate> _projectedOpenSubject = new Subject<ProjectedOpenUpdate>();
        public IObservable<ProjectedOpenUpdate> ProjectedOpenStream => _projectedOpenSubject.AsObservable();

        private IDisposable _projectedOpenSubscription;

        private ProjectedOpenService()
        {
            SetupReactivePipeline();
        }

        private void SetupReactivePipeline()
        {
            var tickerService = TickerService.Instance;

            // Create observables for each data point we need
            var giftChangeStream = tickerService.PriceUpdateStream
                .Where(u => u.TickerSymbol == "GIFT NIFTY" && u.NetChangePercent != 0)
                .Select(u => u.NetChangePercent)
                .DistinctUntilChanged();

            var niftyCloseStream = tickerService.PriceUpdateStream
                .Where(u => u.TickerSymbol == "NIFTY 50" && u.Close > 0)
                .Select(u => u.Close)
                .Take(1);

            var sensexCloseStream = tickerService.PriceUpdateStream
                .Where(u => u.TickerSymbol == "SENSEX" && u.Close > 0)
                .Select(u => u.Close)
                .Take(1);

            // Calculate ONCE when sufficient data is available
            _projectedOpenSubscription = giftChangeStream
                .CombineLatest(niftyCloseStream, sensexCloseStream, (giftChg, niftyClose, sensexClose) => new { giftChg, niftyClose, sensexClose })
                .Take(1)
                .Subscribe(data =>
                {
                    double giftChgDecimal = data.giftChg / 100.0;
                    CalculateAndNotify(data.niftyClose, data.sensexClose, giftChgDecimal);
                    Logger.Info($"[ProjectedOpenService] Rx Pipeline: Initial Projected Opens calculated");
                },
                ex => Logger.Error($"[ProjectedOpenService] Rx Pipeline Error: {ex.Message}", ex));


            // Continuous updates
            // Whenever any relevant ticker updates, recalculate
            tickerService.PriceUpdateStream
                .Where(u => u.TickerSymbol == "GIFT NIFTY" || u.TickerSymbol == "NIFTY 50" || u.TickerSymbol == "SENSEX")
                .Subscribe(_ => CalculateLiveProjectedOpens());
        }

        public void CalculateLiveProjectedOpens()
        {
            // Continuous calculation logic (from CheckAndCalculate)
            var tickerService = TickerService.Instance;
            
            // Need GIFT Price and Prior Close
            double giftPrice = tickerService.GiftNiftyPrice;
            double giftPriorClose = tickerService.GiftNiftyPriorClose;

            if (giftPrice > 0 && giftPriorClose > 0)
            {
                double changePercent = (giftPrice - giftPriorClose) / giftPriorClose;
                
                // We need Spot prices (or closes logic?)
                // MarketAnalyzerLogic uses Spot Price * (1 + change%).
                // But generally Projected Open is Prior Close * (1 + change%).
                // The original code used: double niftyProjected = niftyPrice > 0 ? niftyPrice * (1 + changePercent) : 0;
                // Wait, original code employed NIFTY SPOT PRICE? 
                // Line 529: double niftyProjected = niftyPrice > 0 ? niftyPrice * (1 + changePercent) : 0;
                // where niftyPrice = NiftySpotPrice.
                // This seems odd. Projected Open (for next day) implies using Close?
                // If market is pre-open, NiftySpotPrice might be same as Close.
                
                double niftyPrice = tickerService.NiftySpotPrice > 0 ? tickerService.NiftySpotPrice : tickerService.NiftyTicker.Close;
                double sensexPrice = tickerService.SensexSpotPrice > 0 ? tickerService.SensexSpotPrice : tickerService.SensexTicker.Close;

                if (niftyPrice > 0 && sensexPrice > 0)
                {
                    CalculateAndNotify(niftyPrice, sensexPrice, changePercent);
                }
            }
        }

        private void CalculateAndNotify(double niftyBase, double sensexBase, double changePercentDecimal)
        {
            double niftyProj = niftyBase * (1 + changePercentDecimal);
            double sensexProj = sensexBase * (1 + changePercentDecimal);

            // Update state if changed significantly? 
            // For now just update.
            ProjectedNiftyOpen = niftyProj;
            ProjectedSensexOpen = sensexProj;

            _projectedOpenSubject.OnNext(new ProjectedOpenUpdate
            {
                NiftyProjectedOpen = niftyProj,
                SensexProjectedOpen = sensexProj,
                GiftChangePercent = changePercentDecimal * 100
            });
        }

        public void Reset()
        {
            ProjectedNiftyOpen = 0;
            ProjectedSensexOpen = 0;
            _projectedOpenSubscription?.Dispose();
            SetupReactivePipeline();
        }
    }
}
