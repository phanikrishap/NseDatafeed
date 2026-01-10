using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models.MarketData;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    public class NiftyHistoricalDataLoader
    {
        private const int RANGE_ATR_BARS_TYPE = 7015;
        private const int RANGE_ATR_MINUTE_VALUE = 1;
        private const int RANGE_ATR_MIN_SECONDS = 3;
        private const int RANGE_ATR_MIN_TICKS = 1;
        private const int HISTORICAL_DAYS = 40;
        private const int SLICE_DAYS = 5;
        private const int NUM_SLICES = 8;

        /// <summary>
        /// Loads historical data up to current date (for live mode).
        /// </summary>
        public async Task<(List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)> LoadHistoricalDataAsync(Instrument instrument)
        {
            return await LoadHistoricalDataAsync(instrument, DateTime.Now);
        }

        /// <summary>
        /// Loads historical data up to a specific date (for simulation mode).
        /// Data is loaded from (asOfDate - HISTORICAL_DAYS) to asOfDate.
        /// </summary>
        public async Task<(List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)> LoadHistoricalDataAsync(Instrument instrument, DateTime asOfDate)
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            Logger.Info($"[NiftyHistoricalDataLoader] Starting parallel load - {NUM_SLICES} slices x {SLICE_DAYS} days = {HISTORICAL_DAYS} days (asOf={asOfDate:yyyy-MM-dd})");
            RangeBarLogger.Info($"[PARALLEL_LOAD] Starting: {NUM_SLICES} slices x {SLICE_DAYS} days, symbol={instrument.FullName}, asOf={asOfDate:yyyy-MM-dd}");

            var sliceRanges = new List<(DateTime from, DateTime to, int sliceNum)>();
            var referenceDate = asOfDate;

            for (int i = 0; i < NUM_SLICES; i++)
            {
                var toDate = referenceDate.Date.AddDays(1).AddDays(-i * SLICE_DAYS);
                var fromDate = toDate.AddDays(-SLICE_DAYS);
                sliceRanges.Add((fromDate, toDate, i));
            }

            var allBars = new ConcurrentBag<RangeATRBar>();
            var allTicks = new ConcurrentBag<HistoricalTick>();
            var sliceTasks = new List<Task<(int sliceNum, int bars, int ticks, long elapsedMs, bool success)>>();

            foreach (var slice in sliceRanges)
            {
                var task = RequestSliceAsync(instrument, slice.from, slice.to, slice.sliceNum, allBars, allTicks);
                sliceTasks.Add(task);
            }

            var results = await Task.WhenAll(sliceTasks);
            totalStopwatch.Stop();

            int totalBars = 0, totalTicks = 0, successCount = 0;
            foreach (var result in results.OrderBy(r => r.sliceNum))
            {
                RangeBarLogger.Info($"[SLICE_{result.sliceNum}] Bars={result.bars}, Ticks={result.ticks}, Time={result.elapsedMs}ms, Success={result.success}");
                if (result.success)
                {
                    totalBars += result.bars;
                    totalTicks += result.ticks;
                    successCount++;
                }
            }

            RangeBarLogger.Info($"[PARALLEL_COMPLETE] TotalBars={totalBars}, TotalTicks={totalTicks}, Slices={successCount}/{NUM_SLICES}, TotalTime={totalStopwatch.ElapsedMilliseconds}ms");
            Logger.Info($"[NiftyHistoricalDataLoader] Parallel load complete: {totalBars} bars, {totalTicks} ticks in {totalStopwatch.ElapsedMilliseconds}ms");

            if (successCount == 0)
            {
                return (new List<RangeATRBar>(), new List<HistoricalTick>(), false);
            }

            var sortedBars = allBars.OrderBy(b => b.Time).ToList();
            var sortedTicks = allTicks.OrderBy(t => t.Time).ToList();

            // Re-index bars
            for (int i = 0; i < sortedBars.Count; i++)
                sortedBars[i].Index = i;

            return (sortedBars, sortedTicks, true);
        }

        private async Task<(int sliceNum, int bars, int ticks, long elapsedMs, bool success)> RequestSliceAsync(
            Instrument instrument, DateTime fromDate, DateTime toDate, int sliceNum,
            ConcurrentBag<RangeATRBar> allBars, ConcurrentBag<HistoricalTick> allTicks)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int barsLoaded = 0, ticksLoaded = 0;
            bool success = false;

            try
            {
                var barsComplete = new TaskCompletionSource<bool>();
                var ticksComplete = new TaskCompletionSource<bool>();

                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    // Request RangeATR bars
                    var barsRequest = new BarsRequest(instrument, fromDate, toDate);
                    barsRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = (BarsPeriodType)RANGE_ATR_BARS_TYPE,
                        Value = RANGE_ATR_MINUTE_VALUE,
                        Value2 = RANGE_ATR_MIN_SECONDS,
                        BaseBarsPeriodValue = RANGE_ATR_MIN_TICKS
                    };
                    barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    barsRequest.Request((request, errorCode, errorMessage) =>
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            for (int i = 0; i < request.Bars.Count; i++)
                            {
                                allBars.Add(new RangeATRBar
                                {
                                    Index = i, // Will be re-indexed later
                                    Time = request.Bars.GetTime(i),
                                    Open = request.Bars.GetOpen(i),
                                    High = request.Bars.GetHigh(i),
                                    Low = request.Bars.GetLow(i),
                                    Close = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i)
                                });
                            }
                            barsLoaded = request.Bars.Count;
                            barsComplete.TrySetResult(true);
                        }
                        else
                        {
                            Logger.Warn($"[NiftyHistoricalDataLoader] Slice {sliceNum} bars failed: {errorCode} - {errorMessage}");
                            barsComplete.TrySetResult(false);
                        }
                        request.Dispose();
                    });

                    // Request Tick bars
                    var tickRequest = new BarsRequest(instrument, fromDate, toDate);
                    tickRequest.BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = BarsPeriodType.Tick,
                        Value = 1
                    };
                    tickRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                    tickRequest.Request((request, errorCode, errorMessage) =>
                    {
                        if (errorCode == ErrorCode.NoError && request.Bars != null)
                        {
                            for (int i = 0; i < request.Bars.Count; i++)
                            {
                                allTicks.Add(new HistoricalTick
                                {
                                    Index = i,
                                    Time = request.Bars.GetTime(i),
                                    Price = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i),
                                    IsBuy = request.Bars.GetClose(i) >= request.Bars.GetOpen(i)
                                });
                            }
                            ticksLoaded = request.Bars.Count;
                            ticksComplete.TrySetResult(true);
                        }
                        else
                        {
                            Logger.Warn($"[NiftyHistoricalDataLoader] Slice {sliceNum} ticks failed: {errorCode} - {errorMessage}");
                            ticksComplete.TrySetResult(false);
                        }
                        request.Dispose();
                    });
                });

                var barsResult = await barsComplete.Task;
                var ticksResult = await ticksComplete.Task;
                success = barsResult && ticksResult;
            }
            catch (Exception ex)
            {
                Logger.Error($"[NiftyHistoricalDataLoader] Slice {sliceNum} exception: {ex.Message}", ex);
            }

            stopwatch.Stop();
            return (sliceNum, barsLoaded, ticksLoaded, stopwatch.ElapsedMilliseconds, success);
        }
    }
}
