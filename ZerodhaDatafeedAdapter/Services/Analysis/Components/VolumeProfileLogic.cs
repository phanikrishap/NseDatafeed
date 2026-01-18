using System;
using System.Collections.Generic;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Models.MarketData;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Shared logic for Volume Profile calculation.
    /// Standardizes "Tick-to-Bar" mapping and processing loops used by multiple services.
    /// </summary>
    public static class VolumeProfileLogic
    {
        public delegate void TickProcessor(double price, long volume, bool isBuy, DateTime time);

        /// <summary>
        /// Delegate for tick processing with explicit buy/sell volume split (NinjaTrader 50/50 rule).
        /// </summary>
        public delegate void TickProcessorSplit(double price, long totalVolume, long buyVolume, long sellVolume, DateTime time);

        /// <summary>
        /// Builds an index of ticks for a specific target date to allow fast lookups.
        /// </summary>
        public static List<(DateTime time, int index)> BuildTickTimeIndex(Bars tickBars, DateTime targetDate)
        {
            var tickTimes = new List<(DateTime time, int index)>(tickBars?.Count ?? 0);
            if (tickBars == null) return tickTimes;

            for (int i = 0; i < tickBars.Count; i++)
            {
                var t = tickBars.GetTime(i);
                if (t.Date == targetDate)
                    tickTimes.Add((t, i));
            }
            return tickTimes;
        }

        /// <summary>
        /// Processes ticks within a time window (prevBarTime to currentBarTime) using an optimized search.
        /// Optimized for sequential processing where we maintain the generic 'searchStart' index.
        /// Uses NinjaTrader uptick/downtick rule: price > last = buy, price &lt; last = sell, price == last = 50/50 split.
        /// </summary>
        public static double ProcessTicksOptimized(
            Bars tickBars,
            List<(DateTime time, int index)> tickTimes,
            ref int searchStart,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessor processTickAction)
        {
            while (searchStart < tickTimes.Count)
            {
                var (tickTime, tickIdx) = tickTimes[searchStart];

                // Skip ticks before the previous bar's close (already processed)
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                {
                    searchStart++;
                    continue;
                }

                // Stop if we passed the current bar's close time
                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(tickIdx);
                long volume = tickBars.GetVolume(tickIdx);

                // NinjaTrader uptick/downtick rule - for compatibility, use majority direction
                // Note: For true 50/50 split, use ProcessTicksOptimizedSplit instead
                bool isBuy = price >= lastPrice;

                // Execute the callback for the tick
                processTickAction(price, volume, isBuy, tickTime);

                lastPrice = price;
                searchStart++;
            }

            return lastPrice;
        }

        /// <summary>
        /// Processes ticks with NinjaTrader 50/50 split rule for equal prices.
        /// price > lastPrice: ALL to buy
        /// price &lt; lastPrice: ALL to sell
        /// price == lastPrice: 50% to each
        /// </summary>
        public static double ProcessTicksOptimizedSplit(
            Bars tickBars,
            List<(DateTime time, int index)> tickTimes,
            ref int searchStart,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessorSplit processTickAction)
        {
            while (searchStart < tickTimes.Count)
            {
                var (tickTime, tickIdx) = tickTimes[searchStart];

                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                {
                    searchStart++;
                    continue;
                }

                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(tickIdx);
                long volume = tickBars.GetVolume(tickIdx);

                // NinjaTrader uptick/downtick rule with 50/50 split
                long buyVol, sellVol;
                if (price > lastPrice)
                {
                    buyVol = volume;
                    sellVol = 0;
                }
                else if (price < lastPrice)
                {
                    buyVol = 0;
                    sellVol = volume;
                }
                else // price == lastPrice: 50/50 split
                {
                    buyVol = volume / 2;
                    sellVol = volume - buyVol;
                }

                processTickAction(price, volume, buyVol, sellVol, tickTime);

                lastPrice = price;
                searchStart++;
            }

            return lastPrice;
        }

        /// <summary>
        /// Optimized tick processing for List&lt;HistoricalTick&gt;.
        /// Maintains searchStart index for efficient sequential processing.
        /// </summary>
        public static double ProcessTicksOptimized(
            List<HistoricalTick> ticks,
            ref int searchStart,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessor processTickAction)
        {
            if (ticks == null) return lastPrice;

            while (searchStart < ticks.Count)
            {
                var tick = ticks[searchStart];
                var tickTime = tick.Time;

                // Skip ticks before the previous bar's close
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                {
                    searchStart++;
                    continue;
                }

                // Stop if we passed the current bar's close time
                if (tickTime > currentBarTime) break;

                // Execute the callback for the tick
                processTickAction(tick.Price, tick.Volume, tick.IsBuy, tickTime);

                lastPrice = tick.Price;
                searchStart++;
            }

            return lastPrice;
        }

        /// <summary>
        /// Optimized tick processing for List&lt;HistoricalTick&gt; with pre-computed BuyVolume/SellVolume.
        /// Uses tick.BuyVolume and tick.SellVolume which are set by ApplyUptickDowntickRule with 50/50 split.
        /// </summary>
        public static double ProcessTicksOptimizedSplit(
            List<HistoricalTick> ticks,
            ref int searchStart,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessorSplit processTickAction)
        {
            if (ticks == null) return lastPrice;

            while (searchStart < ticks.Count)
            {
                var tick = ticks[searchStart];
                var tickTime = tick.Time;

                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                {
                    searchStart++;
                    continue;
                }

                if (tickTime > currentBarTime) break;

                // Use pre-computed BuyVolume/SellVolume from ApplyUptickDowntickRule
                processTickAction(tick.Price, tick.Volume, tick.BuyVolume, tick.SellVolume, tickTime);

                lastPrice = tick.Price;
                searchStart++;
            }

            return lastPrice;
        }

        /// <summary>
        /// Processes ticks within a time window for live updates or random access.
        /// Searches linearly from a generic start index.
        /// Note: For compatibility, this still uses isBuy = price >= lastPrice.
        /// </summary>
        public static double ProcessTicksInWindow(
            Bars tickBars,
            int startTickIndex,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessor processTickAction,
            out int lastProcessedIndex)
        {
            lastProcessedIndex = startTickIndex;

            // Ensure valid start index
            int i = startTickIndex + 1;
            if (i < 0) i = 0;

            DateTime today = DateTime.Today;

            for (; i < tickBars.Count; i++)
            {
                var tickTime = tickBars.GetTime(i);

                if (tickTime.Date < today) continue;
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime) continue;
                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(i);
                long volume = tickBars.GetVolume(i);
                bool isBuy = price >= lastPrice;

                processTickAction(price, volume, isBuy, tickTime);

                lastPrice = price;
                lastProcessedIndex = i;
            }

            return lastPrice;
        }

        /// <summary>
        /// Processes ticks within a time window with NinjaTrader 50/50 split rule.
        /// </summary>
        public static double ProcessTicksInWindowSplit(
            Bars tickBars,
            int startTickIndex,
            DateTime prevBarTime,
            DateTime currentBarTime,
            double lastPrice,
            TickProcessorSplit processTickAction,
            out int lastProcessedIndex)
        {
            lastProcessedIndex = startTickIndex;

            int i = startTickIndex + 1;
            if (i < 0) i = 0;

            DateTime today = DateTime.Today;

            for (; i < tickBars.Count; i++)
            {
                var tickTime = tickBars.GetTime(i);

                if (tickTime.Date < today) continue;
                if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime) continue;
                if (tickTime > currentBarTime) break;

                double price = tickBars.GetClose(i);
                long volume = tickBars.GetVolume(i);

                // NinjaTrader uptick/downtick rule with 50/50 split
                long buyVol, sellVol;
                if (price > lastPrice)
                {
                    buyVol = volume;
                    sellVol = 0;
                }
                else if (price < lastPrice)
                {
                    buyVol = 0;
                    sellVol = volume;
                }
                else
                {
                    buyVol = volume / 2;
                    sellVol = volume - buyVol;
                }

                processTickAction(price, volume, buyVol, sellVol, tickTime);

                lastPrice = price;
                lastProcessedIndex = i;
            }

            return lastPrice;
        }
    }
}
