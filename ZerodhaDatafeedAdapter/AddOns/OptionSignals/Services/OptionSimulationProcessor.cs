using System;
using System.Collections.Generic;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Handles simulation replay logic for Option Signals.
    /// Extracted from OptionSignalsViewModel for single responsibility.
    /// </summary>
    public class OptionSimulationProcessor
    {
        private const double VALUE_AREA_PERCENT = 0.70;
        private const double HVN_RATIO = 0.25;

        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        private readonly OptionVPProcessor _vpProcessor;

        public OptionSimulationProcessor(OptionVPProcessor vpProcessor)
        {
            _vpProcessor = vpProcessor;
        }

        /// <summary>
        /// Build simulation replay data for deterministic bar-by-bar replay.
        /// Pre-populates VP with prior day data before building replay bars.
        /// </summary>
        public void BuildReplayData(OptionVPState state, Bars rangeBars,
                                     List<(DateTime time, int index)> tickTimes,
                                     DateTime targetDate)
        {
            var tickBars = state.TickBarsRequest?.Bars;

            // Step 1: Pre-populate VP engines with prior working day's data
            DateTime priorWorkingDay = HolidayCalendarService.Instance.GetPriorWorkingDay(targetDate);

            // Count range bars for diagnostics
            int priorDayRangeBarCount = 0;
            int simDayRangeBarCount = 0;
            for (int i = 0; i < rangeBars.Count; i++)
            {
                var barDate = rangeBars.GetTime(i).Date;
                if (barDate == priorWorkingDay) priorDayRangeBarCount++;
                else if (barDate == targetDate) simDayRangeBarCount++;
            }

            _log.Info($"[SimProcessor] {state.Symbol} BuildReplayData: priorDay={priorWorkingDay:MM-dd}, simDay={targetDate:MM-dd}, " +
                $"priorDayRangeBars={priorDayRangeBarCount}, simDayRangeBars={simDayRangeBarCount}");

            // Build tick index for prior day
            var priorDayTickTimes = new List<(DateTime time, int index)>();
            if (tickBars != null)
            {
                for (int i = 0; i < tickBars.Count; i++)
                {
                    var t = tickBars.GetTime(i);
                    if (t.Date == priorWorkingDay)
                        priorDayTickTimes.Add((t, i));
                }
            }

            _log.Info($"[SimProcessor] {state.Symbol} priorDayTicks={priorDayTickTimes.Count}, simDayTicks={tickTimes.Count}");

            int priorDayBarsProcessed = 0;
            double priorDayLastPrice = 0;

            if (priorDayTickTimes.Count > 0)
            {
                priorDayLastPrice = ProcessPriorDayData(state, rangeBars, tickBars, priorDayTickTimes, priorWorkingDay, out priorDayBarsProcessed);
            }
            else
            {
                _log.Warn($"[SimProcessor] {state.Symbol} no prior day tick data found for {priorWorkingDay:yyyy-MM-dd}");
            }

            // Step 2: Reset session VP for simulation day (fresh session at 9:15)
            state.SessionVPEngine.Reset(0.50);

            // Update row to reflect session reset
            if (state.Type == "CE")
            {
                state.Row.CEHvnBSess = "0";
                state.Row.CEHvnSSess = "0";
                state.Row.CETrendSess = HvnTrend.Neutral;
                state.Row.CETrendSessTime = "";
            }
            else
            {
                state.Row.PEHvnBSess = "0";
                state.Row.PEHvnSSess = "0";
                state.Row.PETrendSess = HvnTrend.Neutral;
                state.Row.PETrendSessTime = "";
            }

            // Reset session trend tracking for new day
            state.LastSessionTrend = HvnTrend.Neutral;
            state.SessionTrendOnsetTime = null;

            // Store last price from prior day
            state.SimLastPrice = priorDayLastPrice > 0 ? priorDayLastPrice : 0;

            // Step 3: Build list of simulation day bars to replay
            state.SimReplayBars = BuildReplayBarList(rangeBars, targetDate);
            state.SimTickTimes = tickTimes;
            state.SimReplayBarIndex = 0;
            state.SimReplayTickIndex = 0;
            state.SimDataReady = true;

            // Step 4: Pre-compute rows for simulation day
            PrecomputeSimulationMetrics(state, targetDate);

            _log.Info($"[SimProcessor] {state.Symbol} replay data ready: {state.SimReplayBars.Count} bars for {targetDate:yyyy-MM-dd}, " +
                $"prior day pre-populated: {priorDayBarsProcessed} bars, pre-computed states: {state.SimPrecomputedRows.Count}");
        }

        /// <summary>
        /// Process a simulated tick - advances through pre-built historical bars.
        /// Called during simulation playback.
        /// </summary>
        public void ProcessSimulatedTick(OptionVPState state, double price, DateTime tickTime)
        {
            if (!state.SimDataReady || state.SimPrecomputedRows == null || state.SimPrecomputedRows.Count == 0)
                return;

            // Find the latest pre-computed row that is applicable for the current tickTime
            OptionSignalsRow applicableRow = null;
            
            // Optimization: since bars are sorted by time, we can track an index
            while (state.SimReplayBarIndex < state.SimPrecomputedRows.Count)
            {
                var row = state.SimPrecomputedRows[state.SimReplayBarIndex];
                string timeStr = state.Type == "CE" ? row.CEAtrTime : row.PEAtrTime;
                
                DateTime barTime;
                if (!DateTime.TryParse(timeStr, out barTime))
                {
                    state.SimReplayBarIndex++;
                    continue;
                }
                
                // For simplicity during simulation of a single day, we can just compare times
                if (barTime.TimeOfDay <= tickTime.TimeOfDay)
                {
                    applicableRow = row;
                    state.SimReplayBarIndex++;
                }
                else
                {
                    break;
                }
            }

            if (applicableRow != null)
            {
                // Update the actual row in the state
                CopyRowData(applicableRow, state.Row);
                
                // Track last bar time for signals
                string closedTimeStr = state.Type == "CE" ? applicableRow.CEAtrTime : applicableRow.PEAtrTime;
                DateTime closedTime;
                if (DateTime.TryParse(closedTimeStr, out closedTime))
                {
                    state.LastBarCloseTime = tickTime.Date.Add(closedTime.TimeOfDay);
                }
            }
        }

        #region Private Helper Methods

        private void PrecomputeSimulationMetrics(OptionVPState state, DateTime targetDate)
        {
            if (state.SimReplayBars == null || state.SimReplayBars.Count == 0) return;

            _log.Info($"[SimProcessor] {state.Symbol} Pre-computing {state.SimReplayBars.Count} metrics for {targetDate:yyyy-MM-dd}");
            state.SimPrecomputedRows.Clear();

            // Save current state (engines)
            var savedSessVp = state.SessionVPEngine.Clone();
            var savedRollVp = state.RollingVPEngine.Clone();
            var savedCdMomo = state.CDMomoEngine.Clone();
            var savedPriceMomo = state.PriceMomoEngine.Clone();
            double savedLastPrice = state.SimLastPrice;
            int savedTickIndex = state.SimReplayTickIndex;
            HvnTrend savedSessTrend = state.LastSessionTrend;
            HvnTrend savedRollTrend = state.LastRollingTrend;

            var tickBars = state.TickBarsRequest?.Bars;

            foreach (var bar in state.SimReplayBars)
            {
                // Start new bar for momentum
                state.CDMomoEngine.StartNewBar();

                // Process ticks for this bar
                ProcessBarTicks(state, bar, tickBars);

                // Update VP engines
                state.SessionVPEngine.SetClosePrice(bar.ClosePrice);
                state.RollingVPEngine.SetClosePrice(bar.ClosePrice);
                state.RollingVPEngine.ExpireOldData(bar.BarTime);
                state.SimLastPrice = bar.ClosePrice;

                // Update ATR display in the temporary Row
                UpdateAtrDisplay(state, bar.ClosePrice, bar.BarTime);

                // Recalculate metrics (this updates state.Row)
                _vpProcessor.RecalculateVP(state, bar.BarTime);

                // Store a copy of the row state
                state.SimPrecomputedRows.Add(state.Row.Clone());
            }

            // Restore state
            state.SessionVPEngine.Restore(savedSessVp);
            state.RollingVPEngine.Restore(savedRollVp);
            state.CDMomoEngine.Restore(savedCdMomo);
            state.PriceMomoEngine.Restore(savedPriceMomo);
            state.SimLastPrice = savedLastPrice;
            state.SimReplayTickIndex = savedTickIndex;
            state.LastSessionTrend = savedSessTrend;
            state.LastRollingTrend = savedRollTrend;

            // Reset replay index for playback
            state.SimReplayBarIndex = 0;
        }

        private void CopyRowData(OptionSignalsRow source, OptionSignalsRow target)
        {
            target.CELTP = source.CELTP;
            target.CETickTime = source.CETickTime;
            target.CEAtrLTP = source.CEAtrLTP;
            target.CEAtrTime = source.CEAtrTime;
            target.CEHvnBSess = source.CEHvnBSess;
            target.CEHvnSSess = source.CEHvnSSess;
            target.CETrendSess = source.CETrendSess;
            target.CETrendSessTime = source.CETrendSessTime;
            target.CEHvnBRoll = source.CEHvnBRoll;
            target.CEHvnSRoll = source.CEHvnSRoll;
            target.CETrendRoll = source.CETrendRoll;
            target.CETrendRollTime = source.CETrendRollTime;
            target.CECDMomo = source.CECDMomo;
            target.CECDSmooth = source.CECDSmooth;
            target.CEPriceMomo = source.CEPriceMomo;
            target.CEPriceSmooth = source.CEPriceSmooth;
            target.CEVwapScoreSess = source.CEVwapScoreSess;
            target.CEVwapScoreRoll = source.CEVwapScoreRoll;

            target.PELTP = source.PELTP;
            target.PETickTime = source.PETickTime;
            target.PEAtrLTP = source.PEAtrLTP;
            target.PEAtrTime = source.PEAtrTime;
            target.PEHvnBSess = source.PEHvnBSess;
            target.PEHvnSSess = source.PEHvnSSess;
            target.PETrendSess = source.PETrendSess;
            target.PETrendSessTime = source.PETrendSessTime;
            target.PEHvnBRoll = source.PEHvnBRoll;
            target.PEHvnSRoll = source.PEHvnSRoll;
            target.PETrendRoll = source.PETrendRoll;
            target.PETrendRollTime = source.PETrendRollTime;
            target.PECDMomo = source.PECDMomo;
            target.PECDSmooth = source.PECDSmooth;
            target.PEPriceMomo = source.PEPriceMomo;
            target.PEPriceSmooth = source.PEPriceSmooth;
            target.PEVwapScoreSess = source.PEVwapScoreSess;
            target.PEVwapScoreRoll = source.PEVwapScoreRoll;
        }


        private double ProcessPriorDayData(OptionVPState state, Bars rangeBars, Bars tickBars,
            List<(DateTime time, int index)> priorDayTickTimes, DateTime priorWorkingDay, out int barsProcessed)
        {
            barsProcessed = 0;
            double lastPrice = 0;
            DateTime prevBarTime = DateTime.MinValue;
            int tickSearchStart = 0;

            for (int barIdx = 0; barIdx < rangeBars.Count; barIdx++)
            {
                DateTime barTime = rangeBars.GetTime(barIdx);

                if (barTime.Date != priorWorkingDay)
                {
                    prevBarTime = barTime;
                    continue;
                }

                double closePrice = rangeBars.GetClose(barIdx);

                // Start new bar for CD momentum
                state.CDMomoEngine.StartNewBar();

                // Process ticks for this bar
                while (tickSearchStart < priorDayTickTimes.Count)
                {
                    var (tickTime, tickIdx) = priorDayTickTimes[tickSearchStart];

                    if (prevBarTime != DateTime.MinValue && tickTime <= prevBarTime)
                    {
                        tickSearchStart++;
                        continue;
                    }

                    if (tickTime > barTime) break;

                    double tickPrice = tickBars.GetClose(tickIdx);
                    long volume = tickBars.GetVolume(tickIdx);
                    bool isBuy = tickPrice >= lastPrice;

                    state.SessionVPEngine.AddTick(tickPrice, volume, isBuy);
                    state.RollingVPEngine.AddTick(tickPrice, volume, isBuy, tickTime);
                    state.CDMomoEngine.AddTick(tickPrice, volume, isBuy, tickTime);

                    lastPrice = tickPrice;
                    tickSearchStart++;
                }

                // Update VP close price
                state.SessionVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.ExpireOldData(barTime);

                // Process Price Momentum
                state.PriceMomoEngine.ProcessBar(closePrice, barTime);

                // Close CD momentum bar
                state.CDMomoEngine.CloseBar(barTime);

                prevBarTime = barTime;
                lastPrice = closePrice;
                barsProcessed++;
            }

            // Calculate final VP state and update display
            if (barsProcessed > 0)
            {
                var sessResult = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                var rollResult = state.RollingVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                state.LastSessionTrend = DetermineTrend(sessResult);
                state.LastRollingTrend = DetermineTrend(rollResult);
                state.LastClosePrice = lastPrice;

                // Update row with prior day's final metrics
                if (state.Type == "CE")
                {
                    state.Row.CEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                    state.Row.CEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                    state.Row.CETrendSess = state.LastSessionTrend;
                    state.Row.CEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                    state.Row.CEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                    state.Row.CETrendRoll = state.LastRollingTrend;
                }
                else
                {
                    state.Row.PEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                    state.Row.PEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                    state.Row.PETrendSess = state.LastSessionTrend;
                    state.Row.PEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                    state.Row.PEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                    state.Row.PETrendRoll = state.LastRollingTrend;
                }

                _log.Info($"[SimProcessor] {state.Symbol} prior day {priorWorkingDay:yyyy-MM-dd} processed: {barsProcessed} bars, " +
                    $"SessHVN B={sessResult.HVNBuyCount}/S={sessResult.HVNSellCount}, RollHVN B={rollResult.HVNBuyCount}/S={rollResult.HVNSellCount}");
            }

            return lastPrice;
        }

        private List<(DateTime BarTime, double ClosePrice, DateTime PrevBarTime)> BuildReplayBarList(Bars rangeBars, DateTime targetDate)
        {
            var replayBars = new List<(DateTime BarTime, double ClosePrice, DateTime PrevBarTime)>();
            DateTime prevSimBarTime = DateTime.MinValue;

            for (int barIdx = 0; barIdx < rangeBars.Count; barIdx++)
            {
                DateTime barTime = rangeBars.GetTime(barIdx);

                if (barTime.Date != targetDate)
                {
                    prevSimBarTime = barTime;
                    continue;
                }

                double closePrice = rangeBars.GetClose(barIdx);
                replayBars.Add((barTime, closePrice, prevSimBarTime));
                prevSimBarTime = barTime;
            }

            return replayBars;
        }

        private void ProcessBarTicks(OptionVPState state, (DateTime BarTime, double ClosePrice, DateTime PrevBarTime) bar, Bars tickBars)
        {
            while (state.SimReplayTickIndex < state.SimTickTimes.Count)
            {
                var (tTime, tIdx) = state.SimTickTimes[state.SimReplayTickIndex];

                if (bar.PrevBarTime != DateTime.MinValue && tTime <= bar.PrevBarTime)
                {
                    state.SimReplayTickIndex++;
                    continue;
                }

                if (tTime > bar.BarTime)
                    break;

                double tPrice = tickBars.GetClose(tIdx);
                long tVolume = tickBars.GetVolume(tIdx);
                bool isBuy = tPrice >= state.SimLastPrice;

                state.SessionVPEngine.AddTick(tPrice, tVolume, isBuy);
                state.RollingVPEngine.AddTick(tPrice, tVolume, isBuy, tTime);
                state.CDMomoEngine.AddTick(tPrice, tVolume, isBuy, tTime);

                state.SimLastPrice = tPrice;
                state.SimReplayTickIndex++;
            }
        }

        private void UpdateAtrDisplay(OptionVPState state, double close, DateTime barTime)
        {
            string timeStr = barTime.ToString("HH:mm:ss");
            if (state.Type == "CE")
            {
                state.Row.CEAtrLTP = close.ToString("F2");
                state.Row.CEAtrTime = timeStr;
            }
            else
            {
                state.Row.PEAtrLTP = close.ToString("F2");
                state.Row.PEAtrTime = timeStr;
            }
        }

        private HvnTrend DetermineTrend(VPResult result)
        {
            if (!result.IsValid) return HvnTrend.Neutral;
            if (result.HVNBuyCount > result.HVNSellCount) return HvnTrend.Bullish;
            if (result.HVNSellCount > result.HVNBuyCount) return HvnTrend.Bearish;
            return HvnTrend.Neutral;
        }

        #endregion
    }
}
