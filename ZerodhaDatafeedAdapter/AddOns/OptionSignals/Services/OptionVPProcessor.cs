using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models;
using ZerodhaDatafeedAdapter.Helpers;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services;
using ZerodhaDatafeedAdapter.Services.Analysis;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Services
{
    /// <summary>
    /// Handles VP (Volume Profile) calculation, HVN trends, and momentum processing.
    /// Extracted from OptionSignalsViewModel for single responsibility.
    /// </summary>
    public class OptionVPProcessor
    {
        private const double VALUE_AREA_PERCENT = 0.70;
        private const double HVN_RATIO = 0.25;

        private static readonly ILoggerService _log = LoggerFactory.OpSignals;

        // Reference to orchestrator for signal evaluation
        public SignalsOrchestrator SignalsOrchestrator { get; set; }

        // Reference to simulation processor for delegation
        private OptionSimulationProcessor _simProcessor;

        // Market context for signal evaluation
        private double _atmStrike;
        private int _strikeStep = 50;
        private DateTime? _selectedExpiry;
        private string _underlying = "NIFTY";

        // Track last CSV write time to avoid writing too frequently
        private DateTime _lastCsvWriteTime = DateTime.MinValue;
        private readonly object _csvWriteLock = new object();

        // Reference to rows dictionary for CSV writing
        private ConcurrentDictionary<string, OptionSignalsRow> _rowsByStrike;

        public void SetSimulationProcessor(OptionSimulationProcessor simProcessor)
        {
            _simProcessor = simProcessor;
        }

        public void SetMarketContext(double atmStrike, int strikeStep, DateTime? expiry, string underlying)
        {
            _atmStrike = atmStrike;
            _strikeStep = strikeStep;
            _selectedExpiry = expiry;
            _underlying = underlying;
        }

        /// <summary>
        /// Sets the rows dictionary reference for CSV writing.
        /// </summary>
        public void SetRowsDictionary(ConcurrentDictionary<string, OptionSignalsRow> rowsByStrike)
        {
            _rowsByStrike = rowsByStrike;
        }

        /// <summary>
        /// Process historical data for a VP state.
        /// Called when both tick and range data are ready.
        /// </summary>
        public void ProcessHistoricalData(OptionVPState state)
        {
            var rangeBars = state.RangeBarsRequest?.Bars;
            var tickBars = state.TickBarsRequest?.Bars;

            if (rangeBars == null || rangeBars.Count == 0)
            {
                _log.Warn($"[VPProcessor] {state.Symbol} no range bars available");
                return;
            }

            if (tickBars == null || tickBars.Count == 0)
            {
                _log.Warn($"[VPProcessor] {state.Symbol} no tick bars available, updating ATR only");
                int lastBarIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastBarIdx), rangeBars.GetTime(lastBarIdx));
                state.LastRangeBarIndex = lastBarIdx;
                state.LastBarCloseTime = rangeBars.GetTime(lastBarIdx);
                return;
            }

            // Determine target date
            DateTime targetDate = GetTargetDataDate();
            bool isSimMode = SimulationService.Instance.IsSimulationMode;

            _log.Info($"[VPProcessor] {state.Symbol} ProcessHistoricalData: targetDate={targetDate:yyyy-MM-dd}, isSimMode={isSimMode}, rangeBars={rangeBars.Count}, tickBars={tickBars.Count}");

            // Build tick time index for target date
            var tickTimes = BuildTickTimeIndex(tickBars, targetDate);

            if (tickTimes.Count == 0)
            {
                _log.Warn($"[VPProcessor] {state.Symbol} no ticks for targetDate={targetDate:yyyy-MM-dd}");
                int lastBarIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastBarIdx), rangeBars.GetTime(lastBarIdx));
                state.LastRangeBarIndex = lastBarIdx;
                state.LastBarCloseTime = rangeBars.GetTime(lastBarIdx);
                return;
            }

            if (isSimMode && _simProcessor != null)
            {
                _log.Info($"[VPProcessor] {state.Symbol} Delegating to SimulationProcessor for replay data");
                _simProcessor.BuildReplayData(state, rangeBars, tickTimes, targetDate);
                return;
            }

            // Live mode: process all historical bars
            ProcessHistoricalBars(state, rangeBars, tickBars, tickTimes, targetDate);
        }

        /// <summary>
        /// Process historical bars for live mode.
        /// </summary>
        private void ProcessHistoricalBars(OptionVPState state, Bars rangeBars, Bars tickBars,
            List<(DateTime time, int index)> tickTimes, DateTime targetDate)
        {
            DateTime prevBarTime = DateTime.MinValue;
            int barsProcessed = 0;
            double lastPrice = 0;
            int tickSearchStart = 0;

            for (int barIdx = 0; barIdx < rangeBars.Count; barIdx++)
            {
                DateTime barTime = rangeBars.GetTime(barIdx);

                if (barTime.Date != targetDate)
                {
                    prevBarTime = barTime;
                    continue;
                }

                double closePrice = rangeBars.GetClose(barIdx);

                // Process ticks for this bar
                lastPrice = ProcessTicksOptimized(state, tickBars, tickTimes, ref tickSearchStart, prevBarTime, barTime, lastPrice);

                // Update VP close price and expire old rolling data
                state.SessionVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.SetClosePrice(closePrice);
                state.RollingVPEngine.ExpireOldData(barTime);
                state.LastClosePrice = closePrice;

                // Recalculate VP
                RecalculateVP(state, barTime);

                prevBarTime = barTime;
                barsProcessed++;
            }

            state.LastBarCloseTime = prevBarTime;
            state.LastRangeBarIndex = rangeBars.Count - 1;
            state.LastClosePrice = lastPrice;

            if (rangeBars.Count > 0)
            {
                int lastIdx = rangeBars.Count - 1;
                UpdateAtrDisplay(state, rangeBars.GetClose(lastIdx), rangeBars.GetTime(lastIdx));
            }

            _log.Info($"[VPProcessor] {state.Symbol} historical processing complete: {barsProcessed} bars");
        }

        /// <summary>
        /// Handle real-time bar update.
        /// </summary>
        public void ProcessRealTimeBar(OptionVPState state, BarsUpdateEventArgs e)
        {
            var bars = state.RangeBarsRequest?.Bars;
            if (bars == null) return;

            int closedBarIndex = e.MaxIndex - 1;
            if (closedBarIndex < 0 || closedBarIndex <= state.LastRangeBarIndex) return;

            double close = bars.GetClose(closedBarIndex);
            DateTime barTime = bars.GetTime(closedBarIndex);

            // Update ATR display
            UpdateAtrDisplay(state, close, barTime);

            // Process ticks from previous bar close to current bar close
            var tickBars = state.TickBarsRequest?.Bars;
            if (tickBars != null && tickBars.Count > 0)
            {
                ProcessTicksInWindow(state, tickBars, state.LastBarCloseTime, barTime, state.LastClosePrice);
            }

            // Update VP and recalculate
            state.SessionVPEngine.SetClosePrice(close);
            state.RollingVPEngine.SetClosePrice(close);
            state.RollingVPEngine.ExpireOldData(barTime);
            state.LastClosePrice = close;

            RecalculateVP(state, barTime);

            state.LastBarCloseTime = barTime;
            state.LastRangeBarIndex = closedBarIndex;
        }

        /// <summary>
        /// Recalculate VP metrics and update row.
        /// Returns the accurate cumulative delta value from CloseBar.
        /// </summary>
        public long RecalculateVP(OptionVPState state, DateTime currentTime)
        {
            // Calculate Session VP
            var sessResult = state.SessionVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);
            var rollResult = state.RollingVPEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

            // Get momentum results - use CloseBarWithDelta to get accurate cumulative delta
            var (cdMomoResult, cumulativeDelta) = state.CDMomoEngine.CloseBarWithDelta(currentTime);
            var priceMomoResult = state.PriceMomoEngine.ProcessBar(state.LastClosePrice, currentTime);

            // Determine trends
            HvnTrend sessTrend = DetermineTrend(sessResult);
            HvnTrend rollTrend = DetermineTrend(rollResult);

            // Calculate VWAP scores
            int sessVwapScore = VWAPScoreCalculator.CalculateScore(state.LastClosePrice, sessResult);
            int rollVwapScore = VWAPScoreCalculator.CalculateScore(state.LastClosePrice, rollResult);

            // Track trend times
            string sessTrendTime = state.Type == "CE" ? state.Row.CETrendSessTime : state.Row.PETrendSessTime;
            string rollTrendTime = state.Type == "CE" ? state.Row.CETrendRollTime : state.Row.PETrendRollTime;

            if (sessTrend != state.LastSessionTrend && sessTrend != HvnTrend.Neutral)
            {
                state.SessionTrendOnsetTime = currentTime;
                sessTrendTime = currentTime.ToString("HH:mm:ss");
                _log.Info($"[VPProcessor] {state.Symbol} Session trend changed to {sessTrend} at {sessTrendTime}");
            }
            state.LastSessionTrend = sessTrend;

            if (rollTrend != state.LastRollingTrend && rollTrend != HvnTrend.Neutral)
            {
                state.RollingTrendOnsetTime = currentTime;
                rollTrendTime = currentTime.ToString("HH:mm:ss");
                _log.Info($"[VPProcessor] {state.Symbol} Rolling trend changed to {rollTrend} at {rollTrendTime}");
            }
            state.LastRollingTrend = rollTrend;

            // Update row metrics based on type
            UpdateRowMetrics(state, sessResult, rollResult, cdMomoResult, priceMomoResult,
                            sessTrend, rollTrend, sessTrendTime, rollTrendTime, sessVwapScore, rollVwapScore);

            // Store bar snapshot
            StoreBarSnapshot(state, sessResult, rollResult, cdMomoResult, priceMomoResult,
                            sessTrend, rollTrend, sessVwapScore, rollVwapScore, currentTime);

            // Feed to SignalsOrchestrator
            NotifySignalsOrchestrator(state, sessResult, rollResult, cdMomoResult, priceMomoResult,
                                     sessTrend, rollTrend, sessVwapScore, rollVwapScore, currentTime);

            // Write to CSV if enabled and this is an ATM/ITM1/OTM1 strike
            TryWriteOptionsSignalsCsv(state, currentTime, sessResult, rollResult);

            return cumulativeDelta;
        }

        /// <summary>
        /// Process a simulated tick - delegates to simulation processor.
        /// </summary>
        public void ProcessSimulatedTick(OptionVPState state, double price, DateTime tickTime)
        {
            if (_simProcessor != null)
            {
                _simProcessor.ProcessSimulatedTick(state, price, tickTime);
            }
        }

        #region Helper Methods

        private List<(DateTime time, int index)> BuildTickTimeIndex(Bars tickBars, DateTime targetDate)
        {
            return VolumeProfileLogic.BuildTickTimeIndex(tickBars, targetDate);
        }

        private double ProcessTicksOptimized(OptionVPState state, Bars tickBars,
            List<(DateTime time, int index)> tickTimes, ref int searchStart,
            DateTime prevBarTime, DateTime currentBarTime, double lastPrice)
        {
            // Start new bar for CD momentum engine and reset volume tracking
            state.CDMomoEngine.StartNewBar();
            state.ResetBarVolume();

            double updatedPrice = VolumeProfileLogic.ProcessTicksOptimized(
                tickBars, tickTimes, ref searchStart, prevBarTime, currentBarTime, lastPrice,
                (price, volume, isBuy, tickTime) =>
                {
                    state.SessionVPEngine.AddTick(price, volume, isBuy);
                    state.RollingVPEngine.AddTick(price, volume, isBuy, tickTime);
                    state.CDMomoEngine.AddTick(price, volume, isBuy, tickTime);
                    state.AddTickVolume(volume, isBuy);
                });

            state.LastVPTickIndex = searchStart > 0 ? tickTimes[searchStart - 1].index : -1;
            return updatedPrice;
        }

        private void ProcessTicksInWindow(OptionVPState state, Bars tickBars, DateTime prevBarTime, DateTime currentBarTime, double lastPrice)
        {
            // Start new bar for CD momentum engine and reset volume tracking
            state.CDMomoEngine.StartNewBar();
            state.ResetBarVolume();

            int lastIndex;
            double updatedPrice = VolumeProfileLogic.ProcessTicksInWindow(
                tickBars, state.LastVPTickIndex, prevBarTime, currentBarTime, lastPrice,
                (price, volume, isBuy, tickTime) =>
                {
                    state.SessionVPEngine.AddTick(price, volume, isBuy);
                    state.RollingVPEngine.AddTick(price, volume, isBuy, tickTime);
                    state.CDMomoEngine.AddTick(price, volume, isBuy, tickTime);
                    state.AddTickVolume(volume, isBuy);
                },
                out lastIndex);

            state.LastClosePrice = updatedPrice;
            state.LastVPTickIndex = lastIndex;
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

        private void UpdateRowMetrics(OptionVPState state, VPResult sessResult, VPResult rollResult,
            MomentumResult cdMomoResult, MomentumResult priceMomoResult,
            HvnTrend sessTrend, HvnTrend rollTrend, string sessTrendTime, string rollTrendTime,
            int sessVwapScore, int rollVwapScore)
        {
            var row = state.Row;

            if (state.Type == "CE")
            {
                row.CEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                row.CEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                row.CETrendSess = sessTrend;
                row.CETrendSessTime = sessTrendTime;
                row.CEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                row.CEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                row.CETrendRoll = rollTrend;
                row.CETrendRollTime = rollTrendTime;
                row.CECDMomo = FormatMomentum(cdMomoResult.Momentum);
                row.CECDSmooth = FormatMomentum(cdMomoResult.Smooth);
                row.CEPriceMomo = FormatMomentum(priceMomoResult.Momentum);
                row.CEPriceSmooth = FormatMomentum(priceMomoResult.Smooth);
                row.CEVwapScoreSess = sessVwapScore;
                row.CEVwapScoreRoll = rollVwapScore;
            }
            else
            {
                row.PEHvnBSess = sessResult.IsValid ? sessResult.HVNBuyCount.ToString() : "0";
                row.PEHvnSSess = sessResult.IsValid ? sessResult.HVNSellCount.ToString() : "0";
                row.PETrendSess = sessTrend;
                row.PETrendSessTime = sessTrendTime;
                row.PEHvnBRoll = rollResult.IsValid ? rollResult.HVNBuyCount.ToString() : "0";
                row.PEHvnSRoll = rollResult.IsValid ? rollResult.HVNSellCount.ToString() : "0";
                row.PETrendRoll = rollTrend;
                row.PETrendRollTime = rollTrendTime;
                row.PECDMomo = FormatMomentum(cdMomoResult.Momentum);
                row.PECDSmooth = FormatMomentum(cdMomoResult.Smooth);
                row.PEPriceMomo = FormatMomentum(priceMomoResult.Momentum);
                row.PEPriceSmooth = FormatMomentum(priceMomoResult.Smooth);
                row.PEVwapScoreSess = sessVwapScore;
                row.PEVwapScoreRoll = rollVwapScore;
            }
        }

        private void StoreBarSnapshot(OptionVPState state, VPResult sessResult, VPResult rollResult,
            MomentumResult cdMomoResult, MomentumResult priceMomoResult,
            HvnTrend sessTrend, HvnTrend rollTrend, int sessVwapScore, int rollVwapScore, DateTime currentTime)
        {
            if (state.BarHistory == null) return;

            var snapshot = new OptionBarSnapshot
            {
                BarTime = currentTime,
                ClosePrice = state.LastClosePrice,
                SessHvnB = sessResult.IsValid ? sessResult.HVNBuyCount : 0,
                SessHvnS = sessResult.IsValid ? sessResult.HVNSellCount : 0,
                SessTrend = sessTrend,
                RollHvnB = rollResult.IsValid ? rollResult.HVNBuyCount : 0,
                RollHvnS = rollResult.IsValid ? rollResult.HVNSellCount : 0,
                RollTrend = rollTrend,
                CDMomentum = cdMomoResult.Momentum,
                CDSmooth = cdMomoResult.Smooth,
                CDBias = cdMomoResult.Bias,
                PriceMomentum = priceMomoResult.Momentum,
                PriceSmooth = priceMomoResult.Smooth,
                PriceBias = priceMomoResult.Bias,
                VwapScoreSess = sessVwapScore,
                VwapScoreRoll = rollVwapScore,
                SessionVWAP = sessResult.VWAP,
                RollingVWAP = rollResult.VWAP,

                // Cumulative Delta (raw value from CD engine)
                CumulativeDelta = state.CDMomoEngine.CurrentCumulativeDelta,

                // Session SD Bands
                SessStdDev = sessResult.IsValid ? sessResult.StdDev : 0,
                SessUpper1SD = sessResult.IsValid ? sessResult.Upper1SD : 0,
                SessUpper2SD = sessResult.IsValid ? sessResult.Upper2SD : 0,
                SessLower1SD = sessResult.IsValid ? sessResult.Lower1SD : 0,
                SessLower2SD = sessResult.IsValid ? sessResult.Lower2SD : 0,

                // Rolling SD Bands
                RollStdDev = rollResult.IsValid ? rollResult.StdDev : 0,
                RollUpper1SD = rollResult.IsValid ? rollResult.Upper1SD : 0,
                RollUpper2SD = rollResult.IsValid ? rollResult.Upper2SD : 0,
                RollLower1SD = rollResult.IsValid ? rollResult.Lower1SD : 0,
                RollLower2SD = rollResult.IsValid ? rollResult.Lower2SD : 0
            };

            state.BarHistory.AddBar(snapshot);
        }

        private void NotifySignalsOrchestrator(OptionVPState state, VPResult sessResult, VPResult rollResult,
            MomentumResult cdMomoResult, MomentumResult priceMomoResult,
            HvnTrend sessTrend, HvnTrend rollTrend, int sessVwapScore, int rollVwapScore, DateTime currentTime)
        {
            if (SignalsOrchestrator == null) return;

            int dte = _selectedExpiry.HasValue ? (_selectedExpiry.Value.Date - DateTime.Today).Days : 0;
            Moneyness moneyness = DetermineMoneyness(state.Row.Strike, state.Type);

            var snapshot = new OptionStateSnapshot
            {
                Symbol = state.Symbol,
                Strike = state.Row.Strike,
                OptionType = state.Type,
                Moneyness = moneyness,
                DTE = dte,
                LastPrice = state.LastClosePrice,
                LastPriceTime = currentTime,
                RangeBarClosePrice = state.LastClosePrice,
                RangeBarCloseTime = currentTime,
                SessHvnB = sessResult.IsValid ? sessResult.HVNBuyCount : 0,
                SessHvnS = sessResult.IsValid ? sessResult.HVNSellCount : 0,
                SessTrend = sessTrend,
                SessTrendOnsetTime = state.SessionTrendOnsetTime,
                RollHvnB = rollResult.IsValid ? rollResult.HVNBuyCount : 0,
                RollHvnS = rollResult.IsValid ? rollResult.HVNSellCount : 0,
                RollTrend = rollTrend,
                RollTrendOnsetTime = state.RollingTrendOnsetTime,
                CDMomentum = cdMomoResult.Momentum,
                CDSmooth = cdMomoResult.Smooth,
                CDBias = cdMomoResult.Bias,
                PriceMomentum = priceMomoResult.Momentum,
                PriceSmooth = priceMomoResult.Smooth,
                PriceBias = priceMomoResult.Bias,
                VwapScoreSess = sessVwapScore,
                VwapScoreRoll = rollVwapScore,
                SessionVWAP = sessResult.VWAP,
                RollingVWAP = rollResult.VWAP
            };

            SignalsOrchestrator.UpdateOptionState(snapshot, state.BarHistory);
        }

        private Moneyness DetermineMoneyness(double strike, string optionType)
        {
            int strikeDiff = (int)((strike - _atmStrike) / _strikeStep);

            if (optionType == "CE")
            {
                if (strikeDiff == 0) return Moneyness.ATM;
                if (strikeDiff == -1) return Moneyness.ITM1;
                if (strikeDiff <= -2 && strikeDiff >= -3) return Moneyness.ITM2;
                if (strikeDiff < -3) return Moneyness.DeepITM;
                if (strikeDiff == 1) return Moneyness.OTM1;
                if (strikeDiff >= 2 && strikeDiff <= 3) return Moneyness.OTM2;
                return Moneyness.DeepOTM;
            }
            else
            {
                if (strikeDiff == 0) return Moneyness.ATM;
                if (strikeDiff == 1) return Moneyness.ITM1;
                if (strikeDiff >= 2 && strikeDiff <= 3) return Moneyness.ITM2;
                if (strikeDiff > 3) return Moneyness.DeepITM;
                if (strikeDiff == -1) return Moneyness.OTM1;
                if (strikeDiff <= -2 && strikeDiff >= -3) return Moneyness.OTM2;
                return Moneyness.DeepOTM;
            }
        }

        private string FormatMomentum(double momentum)
        {
            double abs = Math.Abs(momentum);
            if (abs < 0.1) return "0";

            if (abs >= 1_000_000)
            {
                double val = momentum / 1_000_000.0;
                return Math.Abs(val) >= 10 ? val.ToString("F0") + "M" : val.ToString("F1") + "M";
            }
            else if (abs >= 1_000)
            {
                double val = momentum / 1_000.0;
                return Math.Abs(val) >= 10 ? val.ToString("F0") + "K" : val.ToString("F1") + "K";
            }
            else
            {
                return momentum.ToString("F1");
            }
        }

        private DateTime GetTargetDataDate()
        {
            if (SimulationService.Instance.IsSimulationMode)
            {
                var simConfig = SimulationService.Instance.CurrentConfig;
                if (simConfig != null)
                {
                    return simConfig.SimulationDate.Date;
                }
            }

            DateTime today = DateTime.Today;

            if (DateTimeHelper.IsNonTradingDay(today))
            {
                return HolidayCalendarService.Instance.GetPriorWorkingDay(today);
            }

            if (DateTimeHelper.IsPreMarket())
            {
                return HolidayCalendarService.Instance.GetPriorWorkingDay(today);
            }

            return today;
        }

        /// <summary>
        /// Writes to OptionsSignals.csv when an ATM, ITM1, or OTM1 strike bar closes.
        /// Also handles individual strike CSV writing for qualified symbols.
        /// Throttles writes to at most once per minute boundary or bar close.
        /// </summary>
        private void TryWriteOptionsSignalsCsv(OptionVPState state, DateTime currentTime, VPResult sessResult, VPResult rollResult)
        {
            // Skip CSV writing during pre-computation phase - pre-computation generates row states,
            // actual CSV writing happens during prior day processing and simulation playback
            if (state.IsPrecomputing) return;

            var csvService = CsvReportService.Instance;

            // Check if this is an ATM, ITM1, or OTM1 strike
            int strike = (int)state.Row.Strike;
            int atmStrike = (int)_atmStrike;
            bool isATM = strike == atmStrike;
            bool isITM1orOTM1 = Math.Abs(strike - atmStrike) == _strikeStep;
            bool isQualifyingStrike = isATM || isITM1orOTM1;

            // Register qualifying symbols for individual strike tracking
            if (isQualifyingStrike && csvService.IsIndividualStrikesEnabled)
            {
                csvService.RegisterQualifiedSymbol(state.Symbol);
            }

            // Write individual strike CSV if the symbol is qualified (even if no longer ATM/ITM1/OTM1)
            if (csvService.IsIndividualStrikesEnabled && csvService.IsSymbolQualified(state.Symbol))
            {
                // Get cumulative delta from the CD momentum engine
                long cumulativeDelta = state.CDMomoEngine.CurrentCumulativeDelta;
                csvService.WriteIndividualStrikeRow(state.Symbol, currentTime, state.Type, state.Row,
                    cumulativeDelta, sessResult, rollResult,
                    state.CurrentBarVolume, state.CurrentBarBuyVolume, state.CurrentBarSellVolume, state.CurrentBarDelta);
            }

            // For aggregate OptionsSignals.csv, only write for currently qualifying strikes
            if (!csvService.IsEnabled || !isQualifyingStrike) return;

            // Check if we should write (throttle to prevent excessive writes)
            lock (_csvWriteLock)
            {
                // Write on bar close if at least 1 second has passed since last write
                // Or write on 1-minute boundaries
                bool shouldWrite = false;

                // Write if 1 second has passed since last write (bar close throttle)
                if ((currentTime - _lastCsvWriteTime).TotalSeconds >= 1)
                {
                    shouldWrite = true;
                }

                // Also write on 1-minute boundaries
                if (currentTime.Second == 0 && (currentTime - _lastCsvWriteTime).TotalSeconds >= 30)
                {
                    shouldWrite = true;
                }

                if (!shouldWrite) return;

                // We have rows dictionary - write CSV
                if (_rowsByStrike != null && _rowsByStrike.Count > 0)
                {
                    // Convert to dictionary with string keys
                    var rowsDict = new Dictionary<string, OptionSignalsRow>();
                    foreach (var kvp in _rowsByStrike)
                    {
                        rowsDict[kvp.Key] = kvp.Value;
                    }

                    csvService.WriteOptionsSignalsRow(
                        currentTime,
                        _atmStrike,
                        _strikeStep,
                        rowsDict);

                    _lastCsvWriteTime = currentTime;
                }
            }
        }

        #endregion
    }
}
