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

        // Debug symbol for detailed logging
        private const string DEBUG_SYMBOL = "SENSEX2611483300CE";

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

            // Step 2: Reset session VP and momentum engines for simulation day (fresh session at 9:15)
            // CRITICAL: All session-based engines must be reset to avoid carrying over prior day values

            // DEBUG: Log state before reset
            if (state.Symbol == DEBUG_SYMBOL)
            {
                _log.Info($"[DEBUG-RESET] {state.Symbol} BEFORE RESET: CD={state.CDMomoEngine.CurrentCumulativeDelta}, PriceSmooth={state.PriceMomoEngine.CurrentSmooth:F2}");
            }

            state.SessionVPEngine.Reset(0.50);
            state.CDMomoEngine.Reset();       // Reset cumulative delta - must start at 0 for new session
            state.PriceMomoEngine.Reset();    // Reset price momentum for fresh session calculations

            // DEBUG: Log state after reset
            if (state.Symbol == DEBUG_SYMBOL)
            {
                _log.Info($"[DEBUG-RESET] {state.Symbol} AFTER RESET: CD={state.CDMomoEngine.CurrentCumulativeDelta}, PriceSmooth={state.PriceMomoEngine.CurrentSmooth:F2}");
            }

            // Update row to reflect session reset (all session-based metrics start fresh)
            if (state.Type == "CE")
            {
                state.Row.CEHvnBSess = "0";
                state.Row.CEHvnSSess = "0";
                state.Row.CETrendSess = HvnTrend.Neutral;
                state.Row.CETrendSessTime = "";
                state.Row.CECDMomo = "0";
                state.Row.CECDSmooth = "0";
                state.Row.CEPriceMomo = "0";
                state.Row.CEPriceSmooth = "0";
                state.Row.CEVwapScoreSess = 0;
            }
            else
            {
                state.Row.PEHvnBSess = "0";
                state.Row.PEHvnSSess = "0";
                state.Row.PETrendSess = HvnTrend.Neutral;
                state.Row.PETrendSessTime = "";
                state.Row.PECDMomo = "0";
                state.Row.PECDSmooth = "0";
                state.Row.PEPriceMomo = "0";
                state.Row.PEPriceSmooth = "0";
                state.Row.PEVwapScoreSess = 0;
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

            // Write to CSV for any bars that have been passed during this tick
            WriteSimulationBarsToCSV(state, tickTime);
        }

        /// <summary>
        /// Writes pre-computed simulation bars to CSV as they are passed during playback.
        /// </summary>
        private void WriteSimulationBarsToCSV(OptionVPState state, DateTime tickTime)
        {
            if (state.SimPrecomputedBars == null || state.SimPrecomputedBars.Count == 0)
                return;

            var csvService = CsvReportService.Instance;
            if (!csvService.IsIndividualStrikesEnabled)
                return;

            // Register symbol for tracking if not already done
            csvService.RegisterQualifiedSymbol(state.Symbol);

            // Write all bars that have been passed
            while (state.SimCsvBarIndex < state.SimPrecomputedBars.Count)
            {
                var barData = state.SimPrecomputedBars[state.SimCsvBarIndex];

                // Check if this bar's time has been passed
                if (barData.BarTime.TimeOfDay <= tickTime.TimeOfDay)
                {
                    // Write this bar to CSV with simulation date
                    DateTime csvTime = tickTime.Date.Add(barData.BarTime.TimeOfDay);
                    csvService.WriteIndividualStrikeRow(
                        state.Symbol,
                        csvTime,
                        state.Type,
                        barData.Row,
                        barData.CumulativeDelta,
                        barData.SessResult,
                        barData.RollResult,
                        barData.BarVolume,
                        barData.BarBuyVolume,
                        barData.BarSellVolume,
                        barData.BarDelta);

                    state.SimCsvBarIndex++;
                }
                else
                {
                    break;
                }
            }
        }

        #region Private Helper Methods

        private void PrecomputeSimulationMetrics(OptionVPState state, DateTime targetDate)
        {
            if (state.SimReplayBars == null || state.SimReplayBars.Count == 0) return;

            _log.Info($"[SimProcessor] {state.Symbol} Pre-computing {state.SimReplayBars.Count} metrics for {targetDate:yyyy-MM-dd}");
            state.SimPrecomputedRows.Clear();
            state.SimPrecomputedBars.Clear();

            // Mark as pre-computing to prevent CSV writing during this phase
            state.IsPrecomputing = true;

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

                // Process ticks for this bar and get volume breakdown
                var (barVolume, barBuyVolume, barSellVolume, barDelta) = ProcessBarTicks(state, bar, tickBars);

                // Update VP engines
                state.SessionVPEngine.SetClosePrice(bar.ClosePrice);
                state.RollingVPEngine.SetClosePrice(bar.ClosePrice);
                state.RollingVPEngine.ExpireOldData(bar.BarTime);
                state.SimLastPrice = bar.ClosePrice;
                state.LastClosePrice = bar.ClosePrice;  // Required for PriceMomoEngine.ProcessBar in RecalculateVP

                // Update ATR display in the temporary Row
                UpdateAtrDisplay(state, bar.ClosePrice, bar.BarTime);

                // Calculate VP results BEFORE RecalculateVP (which closes the CD bar)
                var sessResult = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                var rollResult = state.RollingVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                // Recalculate metrics (this updates state.Row and closes CD momentum bar)
                // RecalculateVP now returns the accurate cumulative delta from CloseBarWithDelta
                long cumulativeDelta = _vpProcessor.RecalculateVP(state, bar.BarTime);

                // Store a copy of the row state (for backward compatibility)
                state.SimPrecomputedRows.Add(state.Row.Clone());

                // Store full bar data for CSV writing during playback
                state.SimPrecomputedBars.Add(new SimBarData
                {
                    BarTime = bar.BarTime,
                    Row = state.Row.Clone(),
                    CumulativeDelta = cumulativeDelta,
                    SessResult = sessResult,
                    RollResult = rollResult,
                    BarVolume = barVolume,
                    BarBuyVolume = barBuyVolume,
                    BarSellVolume = barSellVolume,
                    BarDelta = barDelta
                });
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

            // Pre-computation complete - allow CSV writing again
            state.IsPrecomputing = false;

            // Reset replay indices for playback
            state.SimReplayBarIndex = 0;
            state.SimCsvBarIndex = 0;
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

            // Get CSV service for writing prior day data
            var csvService = CsvReportService.Instance;
            bool writeToCSV = csvService.IsIndividualStrikesEnabled;

            // Register the symbol for CSV tracking if writing is enabled
            if (writeToCSV)
            {
                csvService.RegisterQualifiedSymbol(state.Symbol);
            }

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

                // Track bar volume breakdown for CSV
                long barTotalVolume = 0;
                long barBuyVolume = 0;
                long barSellVolume = 0;
                int barTickCount = 0;

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

                    // Track volumes for all bars (for CSV)
                    barTotalVolume += volume;
                    if (isBuy) barBuyVolume += volume;
                    else barSellVolume += volume;
                    barTickCount++;

                    // DEBUG: Log individual ticks for specific symbol (first 5 bars only)
                    if (state.Symbol == DEBUG_SYMBOL && barsProcessed < 5 && barTickCount <= 3)
                    {
                        _log.Info($"[DEBUG-TICK] {state.Symbol} Bar#{barsProcessed} Tick#{barTickCount}: Price={tickPrice:F2}, Vol={volume}, isBuy={isBuy}, tickIdx={tickIdx}");
                    }

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
                var priceMomoResult = state.PriceMomoEngine.ProcessBar(closePrice, barTime);

                // Close CD momentum bar - use CloseBarWithDelta to get accurate cumulative delta
                // (CloseBar adds _barDelta to _runningCumulativeDelta but doesn't reset _barDelta,
                // so CurrentCumulativeDelta would double-count if read after CloseBar)
                var (cdMomoResult, cumulativeDelta) = state.CDMomoEngine.CloseBarWithDelta(barTime);

                state.LastClosePrice = closePrice;

                // DEBUG: Log detailed state for specific symbol
                if (state.Symbol == DEBUG_SYMBOL && barsProcessed < 5)
                {
                    var sessVP = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
                    long barDelta = barBuyVolume - barSellVolume;
                    _log.Info($"[DEBUG-PRIOR] {state.Symbol} Bar#{barsProcessed} Time={barTime:HH:mm:ss} Close={closePrice:F2}");
                    _log.Info($"[DEBUG-PRIOR]   Volume: Total={barTotalVolume}, Buy={barBuyVolume}, Sell={barSellVolume}, Delta={barDelta}, Ticks={barTickCount}");
                    _log.Info($"[DEBUG-PRIOR]   CD: CumDelta={cumulativeDelta}, Momo={cdMomoResult.Momentum:F1}, Smooth={cdMomoResult.Smooth:F1}");
                    _log.Info($"[DEBUG-PRIOR]   Price: Momo={priceMomoResult.Momentum:F2}, Smooth={priceMomoResult.Smooth:F2}");
                    _log.Info($"[DEBUG-PRIOR]   VP: VWAP={sessVP.VWAP:F2}, Valid={sessVP.IsValid}");
                }

                // Write prior day bar data to CSV if enabled
                if (writeToCSV)
                {
                    long barDelta = barBuyVolume - barSellVolume;
                    WritePriorDayBarToCSV(state, barTime, closePrice, cumulativeDelta, barTotalVolume, barBuyVolume, barSellVolume, barDelta);
                }

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

        /// <summary>
        /// Writes a single prior day bar's data to the individual strike CSV.
        /// Calculates VP metrics for the bar and writes them to CSV.
        /// </summary>
        /// <param name="cumulativeDelta">Cumulative delta value captured from CloseBar result</param>
        /// <param name="barVolume">Total volume for this bar</param>
        /// <param name="barBuyVolume">Buy volume for this bar</param>
        /// <param name="barSellVolume">Sell volume for this bar</param>
        /// <param name="barDelta">Delta for this bar (buy - sell)</param>
        private void WritePriorDayBarToCSV(OptionVPState state, DateTime barTime, double closePrice, long cumulativeDelta,
            long barVolume, long barBuyVolume, long barSellVolume, long barDelta)
        {
            var csvService = CsvReportService.Instance;
            if (!csvService.IsIndividualStrikesEnabled) return;

            // Calculate VP metrics for this bar
            var sessResult = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
            var rollResult = state.RollingVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

            // Determine trends
            HvnTrend sessTrend = DetermineTrend(sessResult);
            HvnTrend rollTrend = DetermineTrend(rollResult);

            // Calculate VWAP scores
            int sessVwapScore = VWAPScoreCalculator.CalculateScore(closePrice, sessResult);
            int rollVwapScore = VWAPScoreCalculator.CalculateScore(closePrice, rollResult);

            // Update the row with current bar's metrics (for CSV writing)
            UpdateRowForCSV(state, sessResult, rollResult, sessTrend, rollTrend, sessVwapScore, rollVwapScore, barTime, closePrice);

            // Write to CSV - cumulativeDelta and volume data passed in from processing
            csvService.WriteIndividualStrikeRow(state.Symbol, barTime, state.Type, state.Row,
                cumulativeDelta, sessResult, rollResult, barVolume, barBuyVolume, barSellVolume, barDelta);
        }

        /// <summary>
        /// Updates the row metrics for CSV writing during prior day processing.
        /// </summary>
        private void UpdateRowForCSV(OptionVPState state, VPResult sessResult, VPResult rollResult,
            HvnTrend sessTrend, HvnTrend rollTrend, int sessVwapScore, int rollVwapScore,
            DateTime barTime, double closePrice)
        {
            // Get momentum values (these were already processed in the main loop)
            double cdMomo = state.CDMomoEngine.CurrentCumulativeDelta;  // This is the raw CD value
            double priceSmooth = state.PriceMomoEngine.CurrentSmooth;

            string timeStr = barTime.ToString("HH:mm:ss");

            if (state.Type == "CE")
            {
                state.Row.CEAtrTime = timeStr;
                state.Row.CEAtrLTP = closePrice.ToString("F2");
                state.Row.CEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                state.Row.CEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                state.Row.CETrendSess = sessTrend;
                state.Row.CEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                state.Row.CEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                state.Row.CETrendRoll = rollTrend;
                state.Row.CEVwapScoreSess = sessVwapScore;
                state.Row.CEVwapScoreRoll = rollVwapScore;
                // Note: CDMomo/CDSmooth/PriceMomo/PriceSmooth are formatted by _vpProcessor.RecalculateVP
                // For prior day, we'll set them to basic values
                state.Row.CECDMomo = "0";  // These will be more accurate once we integrate with RecalculateVP
                state.Row.CECDSmooth = "0";
                state.Row.CEPriceMomo = "0";
                state.Row.CEPriceSmooth = priceSmooth.ToString("F1");
            }
            else
            {
                state.Row.PEAtrTime = timeStr;
                state.Row.PEAtrLTP = closePrice.ToString("F2");
                state.Row.PEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                state.Row.PEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                state.Row.PETrendSess = sessTrend;
                state.Row.PEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                state.Row.PEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                state.Row.PETrendRoll = rollTrend;
                state.Row.PEVwapScoreSess = sessVwapScore;
                state.Row.PEVwapScoreRoll = rollVwapScore;
                state.Row.PECDMomo = "0";
                state.Row.PECDSmooth = "0";
                state.Row.PEPriceMomo = "0";
                state.Row.PEPriceSmooth = priceSmooth.ToString("F1");
            }
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

        /// <summary>
        /// Processes ticks for a bar and returns volume breakdown.
        /// </summary>
        private (long BarVolume, long BarBuyVolume, long BarSellVolume, long BarDelta) ProcessBarTicks(
            OptionVPState state, (DateTime BarTime, double ClosePrice, DateTime PrevBarTime) bar, Bars tickBars)
        {
            long barVolume = 0;
            long barBuyVolume = 0;
            long barSellVolume = 0;

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

                // Track volume breakdown
                barVolume += tVolume;
                if (isBuy) barBuyVolume += tVolume;
                else barSellVolume += tVolume;

                state.SessionVPEngine.AddTick(tPrice, tVolume, isBuy);
                state.RollingVPEngine.AddTick(tPrice, tVolume, isBuy, tTime);
                state.CDMomoEngine.AddTick(tPrice, tVolume, isBuy, tTime);

                state.SimLastPrice = tPrice;
                state.SimReplayTickIndex++;
            }

            return (barVolume, barBuyVolume, barSellVolume, barBuyVolume - barSellVolume);
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
