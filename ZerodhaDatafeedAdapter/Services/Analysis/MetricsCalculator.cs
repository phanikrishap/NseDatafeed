using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models.MarketData;


namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    public class MetricsCalculator
    {
        /// <summary>
        /// Applies NinjaTrader uptick/downtick rule to ticks with 50/50 split for equal prices.
        /// - price > prevPrice: ALL volume to buy
        /// - price &lt; prevPrice: ALL volume to sell
        /// - price == prevPrice: 50% to each (NinjaTrader standard)
        /// </summary>
        public void ApplyUptickDowntickRule(List<HistoricalTick> ticks)
        {
            if (ticks == null || ticks.Count == 0) return;

            long totalBuyVol = 0;
            long totalSellVol = 0;
            double prevPrice = 0;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                double price = tick.Price;
                long volume = tick.Volume;

                if (i == 0)
                {
                    // First tick: use existing IsBuy or default to buy
                    tick.BuyVolume = tick.IsBuy ? volume : 0;
                    tick.SellVolume = tick.IsBuy ? 0 : volume;
                }
                else if (price > prevPrice)
                {
                    tick.IsBuy = true;
                    tick.BuyVolume = volume;
                    tick.SellVolume = 0;
                }
                else if (price < prevPrice)
                {
                    tick.IsBuy = false;
                    tick.BuyVolume = 0;
                    tick.SellVolume = volume;
                }
                else // price == prevPrice: 50/50 split
                {
                    tick.IsBuy = true; // Majority to buy for backward compat
                    tick.BuyVolume = volume / 2;
                    tick.SellVolume = volume - tick.BuyVolume; // Handles odd volumes
                }

                totalBuyVol += tick.BuyVolume;
                totalSellVol += tick.SellVolume;
                prevPrice = price;
            }

            Logger.Info($"[MetricsCalculator] Tick classification: BuyVol={totalBuyVol:N0}, SellVol={totalSellVol:N0} ({ticks.Count} ticks)");
        }


    }
}
