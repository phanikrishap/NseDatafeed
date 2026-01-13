using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models.MarketData;


namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    public class MetricsCalculator
    {
        public void ApplyUptickDowntickRule(List<HistoricalTick> ticks)
        {
            if (ticks == null || ticks.Count == 0) return;

            int buyCount = 0;
            int sellCount = 0;
            double prevPrice = 0;
            bool lastDirection = true;

            for (int i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                double price = tick.Price;

                if (i == 0)
                {
                    lastDirection = tick.IsBuy;
                }
                else if (price > prevPrice)
                {
                    tick.IsBuy = true;
                    lastDirection = true;
                }
                else if (price < prevPrice)
                {
                    tick.IsBuy = false;
                    lastDirection = false;
                }
                else
                {
                    tick.IsBuy = lastDirection;
                }

                prevPrice = price;

                if (tick.IsBuy) buyCount++;
                else sellCount++;
            }

            Logger.Info($"[MetricsCalculator] Tick classification: Buy={buyCount}, Sell={sellCount} ({ticks.Count} total)");
        }


    }
}
