using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models.Simulation;
using ZerodhaDatafeedAdapter.Services.Analysis;

namespace ZerodhaDatafeedAdapter.Services.Simulation
{
    /// <summary>
    /// Loads NIFTY_I historical data for simulation mode.
    /// This is called by SimulationService during the Loading phase to pre-load all data
    /// required by NiftyFuturesMetricsService before playback begins.
    /// </summary>
    public class NiftyIDataLoader
    {
        private static readonly ILoggerService _log = LoggerFactory.Simulation;

        // Constants matching NiftyFuturesMetricsService
        private const string NIFTY_I_SYMBOL = "NIFTY_I";
        private const int RANGE_ATR_BARS_TYPE = 7015;
        private const int RANGE_ATR_MINUTE_VALUE = 1;
        private const int RANGE_ATR_MIN_SECONDS = 3;
        private const int RANGE_ATR_MIN_TICKS = 1;
        private const int HISTORICAL_DAYS = 40;
        private const int YEARLY_BARS = 252;
        private const int SLICE_DAYS = 5;
        private const int NUM_SLICES = 8;

        /// <summary>
        /// Loads all NIFTY_I data required for simulation.
        /// This includes:
        /// - Prior day RangeATR bars and ticks (for VP/RelMetrics historical buffers)
        /// - Simulation day RangeATR bars and ticks (for progressive replay)
        /// - Daily bars (for ADR/yearly calculations)
        /// </summary>
        /// <param name="simulationDate">The date to simulate</param>
        /// <returns>NiftyISimulationData containing all loaded data</returns>
        public async Task<NiftyISimulationData> LoadDataForSimulationAsync(DateTime simulationDate)
        {
            var result = new NiftyISimulationData
            {
                SimulationDate = simulationDate
            };

            var totalStopwatch = Stopwatch.StartNew();

            try
            {
                _log.Info($"[NiftyIDataLoader] Loading NIFTY_I data for simulation date {simulationDate:yyyy-MM-dd}");

                // Get the NIFTY_I instrument
                Instrument instrument = await GetInstrumentAsync(NIFTY_I_SYMBOL);
                if (instrument == null)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Failed to get instrument for {NIFTY_I_SYMBOL}";
                    _log.Error($"[NiftyIDataLoader] {result.ErrorMessage}");
                    return result;
                }

                _log.Info($"[NiftyIDataLoader] Got instrument: {instrument.FullName}");

                // Load all data in parallel
                var priorDayTask = LoadPriorDaysDataAsync(instrument, simulationDate);
                var simDayTask = LoadSimulationDayDataAsync(instrument, simulationDate);
                var dailyTask = LoadDailyBarsAsync(instrument, simulationDate);

                await Task.WhenAll(priorDayTask, simDayTask, dailyTask);

                var (priorBars, priorTicks, priorSuccess) = await priorDayTask;
                var (simBars, simTicks, simSuccess) = await simDayTask;
                var (dailyBars, dailySuccess) = await dailyTask;

                if (!priorSuccess)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Failed to load prior day data";
                    _log.Error($"[NiftyIDataLoader] {result.ErrorMessage}");
                    return result;
                }

                if (!simSuccess)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Failed to load simulation day data";
                    _log.Error($"[NiftyIDataLoader] {result.ErrorMessage}");
                    return result;
                }

                // Daily bars failure is not critical - continue with warning
                if (!dailySuccess)
                {
                    _log.Warn("[NiftyIDataLoader] Daily bars load failed - composite metrics may be incomplete");
                }

                result.PriorDayBars = priorBars;
                result.PriorDayTicks = priorTicks;
                result.SimulationDayBars = simBars;
                result.SimulationDayTicks = simTicks;
                result.DailyBars = dailyBars;
                result.IsValid = true;

                totalStopwatch.Stop();
                result.LoadTimeMs = totalStopwatch.ElapsedMilliseconds;

                _log.Info($"[NiftyIDataLoader] {result}");
                return result;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                result.IsValid = false;
                result.ErrorMessage = ex.Message;
                result.LoadTimeMs = totalStopwatch.ElapsedMilliseconds;
                _log.Error($"[NiftyIDataLoader] Exception: {ex.Message}", ex);
                return result;
            }
        }

        private async Task<Instrument> GetInstrumentAsync(string symbol)
        {
            var tcs = new TaskCompletionSource<Instrument>();

            await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                var instrument = Instrument.GetInstrument(symbol);
                tcs.TrySetResult(instrument);
            });

            return await tcs.Task;
        }

        /// <summary>
        /// Loads prior day data (everything before simulationDate) for historical VP buffers.
        /// </summary>
        private async Task<(List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)> LoadPriorDaysDataAsync(
            Instrument instrument, DateTime simulationDate)
        {
            var priorEndDate = simulationDate.Date.AddDays(-1); // Day before simulation
            var priorStartDate = priorEndDate.AddDays(-HISTORICAL_DAYS);

            _log.Info($"[NiftyIDataLoader] Loading prior days: {priorStartDate:yyyy-MM-dd} to {priorEndDate:yyyy-MM-dd}");

            return await LoadDateRangeDataAsync(instrument, priorStartDate, priorEndDate.AddDays(1), "Prior");
        }

        /// <summary>
        /// Loads simulation day data only.
        /// </summary>
        private async Task<(List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)> LoadSimulationDayDataAsync(
            Instrument instrument, DateTime simulationDate)
        {
            var simStart = simulationDate.Date;
            var simEnd = simulationDate.Date.AddDays(1);

            _log.Info($"[NiftyIDataLoader] Loading simulation day: {simStart:yyyy-MM-dd}");

            return await LoadDateRangeDataAsync(instrument, simStart, simEnd, "SimDay");
        }

        /// <summary>
        /// Loads data for a specific date range using parallel slice loading.
        /// </summary>
        private async Task<(List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)> LoadDateRangeDataAsync(
            Instrument instrument, DateTime fromDate, DateTime toDate, string label)
        {
            var stopwatch = Stopwatch.StartNew();

            // For single day, don't use slicing
            var daySpan = (toDate - fromDate).TotalDays;
            if (daySpan <= 2)
            {
                var result = await LoadSingleSliceAsync(instrument, fromDate, toDate, 0);
                stopwatch.Stop();
                _log.Info($"[NiftyIDataLoader] {label}: {result.bars} bars, {result.ticks} ticks in {stopwatch.ElapsedMilliseconds}ms");
                return (result.barsList, result.ticksList, result.success);
            }

            // Use parallel slicing for larger date ranges
            var allBars = new ConcurrentBag<RangeATRBar>();
            var allTicks = new ConcurrentBag<HistoricalTick>();

            int numSlices = Math.Min(NUM_SLICES, (int)Math.Ceiling(daySpan / SLICE_DAYS));
            var sliceTasks = new List<Task<(int sliceNum, List<RangeATRBar> bars, List<HistoricalTick> ticks, bool success)>>();

            for (int i = 0; i < numSlices; i++)
            {
                var sliceEnd = toDate.AddDays(-i * SLICE_DAYS);
                var sliceStart = sliceEnd.AddDays(-SLICE_DAYS);
                if (sliceStart < fromDate) sliceStart = fromDate;

                sliceTasks.Add(LoadSingleSliceAsync(instrument, sliceStart, sliceEnd, i)
                    .ContinueWith(t =>
                    {
                        var r = t.Result;
                        foreach (var bar in r.barsList) allBars.Add(bar);
                        foreach (var tick in r.ticksList) allTicks.Add(tick);
                        return (r.sliceNum, r.barsList, r.ticksList, r.success);
                    }));
            }

            await Task.WhenAll(sliceTasks);
            stopwatch.Stop();

            var sortedBars = allBars.OrderBy(b => b.Time).ToList();
            var sortedTicks = allTicks.OrderBy(t => t.Time).ToList();

            // Re-index bars
            for (int i = 0; i < sortedBars.Count; i++)
                sortedBars[i].Index = i;

            bool allSuccess = sliceTasks.All(t => t.Result.success);

            _log.Info($"[NiftyIDataLoader] {label}: {sortedBars.Count} bars, {sortedTicks.Count} ticks in {stopwatch.ElapsedMilliseconds}ms (parallel)");

            return (sortedBars, sortedTicks, allSuccess || sortedBars.Count > 0);
        }

        private async Task<(int sliceNum, List<RangeATRBar> barsList, List<HistoricalTick> ticksList, int bars, int ticks, bool success)> LoadSingleSliceAsync(
            Instrument instrument, DateTime fromDate, DateTime toDate, int sliceNum)
        {
            var barsList = new List<RangeATRBar>();
            var ticksList = new List<HistoricalTick>();
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
                                barsList.Add(new RangeATRBar
                                {
                                    Index = i,
                                    Time = request.Bars.GetTime(i),
                                    Open = request.Bars.GetOpen(i),
                                    High = request.Bars.GetHigh(i),
                                    Low = request.Bars.GetLow(i),
                                    Close = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i)
                                });
                            }
                            barsComplete.TrySetResult(true);
                        }
                        else
                        {
                            _log.Warn($"[NiftyIDataLoader] Slice {sliceNum} bars failed: {errorCode} - {errorMessage}");
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
                                ticksList.Add(new HistoricalTick
                                {
                                    Index = i,
                                    Time = request.Bars.GetTime(i),
                                    Price = request.Bars.GetClose(i),
                                    Volume = request.Bars.GetVolume(i),
                                    IsBuy = request.Bars.GetClose(i) >= request.Bars.GetOpen(i)
                                });
                            }
                            ticksComplete.TrySetResult(true);
                        }
                        else
                        {
                            _log.Warn($"[NiftyIDataLoader] Slice {sliceNum} ticks failed: {errorCode} - {errorMessage}");
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
                _log.Error($"[NiftyIDataLoader] Slice {sliceNum} exception: {ex.Message}", ex);
            }

            return (sliceNum, barsList, ticksList, barsList.Count, ticksList.Count, success);
        }

        /// <summary>
        /// Loads daily bars for ADR and yearly high/low calculations.
        /// </summary>
        private async Task<(List<DailyBar> bars, bool success)> LoadDailyBarsAsync(Instrument instrument, DateTime asOfDate)
        {
            var dailyBars = new List<DailyBar>();
            var tcs = new TaskCompletionSource<bool>();

            _log.Info($"[NiftyIDataLoader] Loading {YEARLY_BARS} daily bars for ADR/yearly calculations");

            await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                var request = new BarsRequest(instrument, YEARLY_BARS);
                request.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = BarsPeriodType.Minute,
                    Value = 1440 // Daily bars
                };
                request.TradingHours = TradingHours.Get("Default 24 x 7");

                request.Request((req, errorCode, errorMessage) =>
                {
                    if (errorCode == ErrorCode.NoError && req.Bars != null)
                    {
                        for (int i = 0; i < req.Bars.Count; i++)
                        {
                            var barTime = req.Bars.GetTime(i);
                            // Only include bars up to and including the day before simulation date
                            if (barTime.Date < asOfDate.Date)
                            {
                                dailyBars.Add(new DailyBar
                                {
                                    Date = barTime,
                                    Open = req.Bars.GetOpen(i),
                                    High = req.Bars.GetHigh(i),
                                    Low = req.Bars.GetLow(i),
                                    Close = req.Bars.GetClose(i),
                                    Volume = (long)req.Bars.GetVolume(i)
                                });
                            }
                        }
                        _log.Info($"[NiftyIDataLoader] Loaded {dailyBars.Count} daily bars (filtered to before {asOfDate:yyyy-MM-dd})");
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        _log.Warn($"[NiftyIDataLoader] Daily bars failed: {errorCode} - {errorMessage}");
                        tcs.TrySetResult(false);
                    }
                    req.Dispose();
                });
            });

            var success = await tcs.Task;
            return (dailyBars, success);
        }
    }
}
