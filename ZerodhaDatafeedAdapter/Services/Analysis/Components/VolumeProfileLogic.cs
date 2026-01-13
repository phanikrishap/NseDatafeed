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
                bool isBuy = price >= lastPrice;

                // Execute the callback for the tick
                processTickAction(price, volume, isBuy, tickTime);

                lastPrice = price;
                searchStart++;
            }

            return lastPrice;
        }

        /// <summary>
        /// Optimized tick processing for List<HistoricalTick>.
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
        /// Processes ticks within a time window for live updates or random access.
        /// Searches linearly from a generic start index.
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
    }
}
