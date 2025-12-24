using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;

namespace QANinjaAdapter.Services.Analysis
{
    /// <summary>
    /// Service that calculates VWAP with Standard Deviation bands for option instruments.
    /// Uses BarsRequest to create a "hidden chart" data series and computes VWAP using
    /// the same algorithm as the VWAPWithStdDevBands indicator.
    /// This follows the "Hidden Calculation Indicator" pattern from NinjaTrader documentation.
    /// </summary>
    public class VWAPCalculatorService
    {
        private static VWAPCalculatorService _instance;
        public static VWAPCalculatorService Instance => _instance ?? (_instance = new VWAPCalculatorService());

        // Active BarsRequests for VWAP calculation (keeps the "hidden chart" alive)
        private readonly ConcurrentDictionary<string, BarsRequest> _vwapBarsRequests = new ConcurrentDictionary<string, BarsRequest>();

        private VWAPCalculatorService()
        {
            Logger.Info("[VWAPCalculatorService] Initialized - using VWAPWithStdDevBands algorithm");
        }

        /// <summary>
        /// Starts VWAP calculation for an instrument using a hidden BarsRequest.
        /// </summary>
        public async Task StartVWAPCalculation(string symbol, Instrument ntInstrument)
        {
            if (_vwapBarsRequests.ContainsKey(symbol))
            {
                Logger.Debug($"[VWAPCalculatorService] VWAP calculation already active for {symbol}");
                return;
            }

            Logger.Info($"[VWAPCalculatorService] Starting VWAP calculation for {symbol}");

            try
            {
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Create BarsRequest for 1-minute data - this is our "hidden chart"
                        // Request enough bars for full trading day VWAP calculation (~375 bars for 6.25 hours)
                        var barsRequest = new BarsRequest(ntInstrument, 500);
                        barsRequest.BarsPeriod = new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Minute,
                            Value = 1
                        };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");

                        string symbolForClosure = symbol;

                        // Subscribe to bar updates - recalculate VWAP on each new bar
                        barsRequest.Update += (sender, e) => OnBarsUpdate(symbolForClosure, e);

                        _vwapBarsRequests.TryAdd(symbol, barsRequest);

                        barsRequest.Request((request, errorCode, errorMessage) =>
                        {
                            if (errorCode == ErrorCode.NoError)
                            {
                                int barCount = request.Bars?.Count ?? 0;
                                Logger.Info($"[VWAPCalculatorService] BarsRequest completed for {symbolForClosure}: {barCount} bars loaded");

                                // Calculate initial VWAP from historical bars
                                if (request.Bars != null && barCount > 0)
                                {
                                    CalculateAndPublishVWAP(symbolForClosure, request.Bars);
                                }
                            }
                            else
                            {
                                Logger.Warn($"[VWAPCalculatorService] BarsRequest failed for {symbolForClosure}: {errorCode} - {errorMessage}");
                            }
                        });

                        Logger.Debug($"[VWAPCalculatorService] BarsRequest sent for {symbol}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[VWAPCalculatorService] StartVWAPCalculation({symbol}): Error - {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"[VWAPCalculatorService] StartVWAPCalculation({symbol}): Dispatcher error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handles bar updates from the hidden BarsRequest - recalculates VWAP
        /// </summary>
        private void OnBarsUpdate(string symbol, BarsUpdateEventArgs e)
        {
            try
            {
                var barsSeries = e.BarsSeries;
                if (barsSeries == null || barsSeries.Count == 0)
                    return;

                // Recalculate VWAP with new bar data
                CalculateAndPublishVWAPFromSeries(symbol, barsSeries);
            }
            catch (Exception ex)
            {
                Logger.Error($"[VWAPCalculatorService] OnBarsUpdate({symbol}): Error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Calculates VWAP from BarsSeries (for Update events)
        /// </summary>
        private void CalculateAndPublishVWAPFromSeries(string symbol, BarsSeries barsSeries)
        {
            try
            {
                if (barsSeries == null || barsSeries.Count == 0)
                    return;

                // Session-based VWAP calculation (matching VWAPWithStdDevBands indicator logic)
                double sumPriceVolume = 0;
                double sumVolume = 0;
                double sumSquaredPriceVolume = 0;
                DateTime sessionDate = DateTime.MinValue;

                DateTime today = DateTime.Today;
                DateTime sessionStart = today.AddHours(9).AddMinutes(15);

                int includedBars = 0;

                for (int i = 0; i < barsSeries.Count; i++)
                {
                    DateTime barTime = barsSeries.GetTime(i);

                    if (barTime.Date != sessionDate)
                    {
                        sessionDate = barTime.Date;
                        sumPriceVolume = 0;
                        sumVolume = 0;
                        sumSquaredPriceVolume = 0;
                    }

                    if (barTime.Date != today || barTime < sessionStart)
                        continue;

                    double high = barsSeries.GetHigh(i);
                    double low = barsSeries.GetLow(i);
                    double close = barsSeries.GetClose(i);
                    double volume = barsSeries.GetVolume(i);

                    if (volume <= 0)
                        continue;

                    double typicalPrice = (high + low + close) / 3.0;
                    sumPriceVolume += typicalPrice * volume;
                    sumVolume += volume;
                    sumSquaredPriceVolume += typicalPrice * typicalPrice * volume;
                    includedBars++;
                }

                if (sumVolume > 0 && includedBars > 0)
                {
                    double vwap = sumPriceVolume / sumVolume;
                    double variance = (sumSquaredPriceVolume / sumVolume) - (vwap * vwap);
                    double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;

                    double sd1Upper = vwap + stdDev;
                    double sd1Lower = vwap - stdDev;
                    double sd2Upper = vwap + (2 * stdDev);
                    double sd2Lower = vwap - (2 * stdDev);

                    VWAPDataCache.Instance.UpdateVWAP(symbol, vwap, sd1Upper, sd1Lower, sd2Upper, sd2Lower);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[VWAPCalculatorService] CalculateAndPublishVWAPFromSeries({symbol}): Error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Calculates VWAP using the same algorithm as VWAPWithStdDevBands indicator
        /// and publishes to the cache
        /// </summary>
        private void CalculateAndPublishVWAP(string symbol, Bars bars)
        {
            try
            {
                if (bars == null || bars.Count == 0)
                    return;

                // Session-based VWAP calculation (matching VWAPWithStdDevBands indicator logic)
                double sumPriceVolume = 0;
                double sumVolume = 0;
                double sumSquaredPriceVolume = 0;
                DateTime sessionDate = DateTime.MinValue;

                // Find today's session start
                DateTime today = DateTime.Today;

                // NSE/BSE market opens at 9:15 AM
                DateTime sessionStart = today.AddHours(9).AddMinutes(15);

                int includedBars = 0;

                for (int i = 0; i < bars.Count; i++)
                {
                    DateTime barTime = bars.GetTime(i);

                    // Check for new session (reset on new day)
                    if (barTime.Date != sessionDate)
                    {
                        sessionDate = barTime.Date;
                        sumPriceVolume = 0;
                        sumVolume = 0;
                        sumSquaredPriceVolume = 0;
                    }

                    // Only include bars from today's trading session
                    if (barTime.Date != today || barTime < sessionStart)
                        continue;

                    double high = bars.GetHigh(i);
                    double low = bars.GetLow(i);
                    double close = bars.GetClose(i);
                    double volume = bars.GetVolume(i);

                    // Skip bars with no volume
                    if (volume <= 0)
                        continue;

                    // Typical Price = (High + Low + Close) / 3 (same as indicator)
                    double typicalPrice = (high + low + close) / 3.0;

                    // Accumulate values (same as indicator)
                    sumPriceVolume += typicalPrice * volume;
                    sumVolume += volume;
                    sumSquaredPriceVolume += typicalPrice * typicalPrice * volume;

                    includedBars++;
                }

                // Calculate VWAP and Standard Deviation (same formula as indicator)
                if (sumVolume > 0 && includedBars > 0)
                {
                    double vwap = sumPriceVolume / sumVolume;
                    double variance = (sumSquaredPriceVolume / sumVolume) - (vwap * vwap);
                    double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;

                    // Calculate SD bands (matching indicator's Values)
                    double sd1Upper = vwap + stdDev;        // Values[2] in indicator
                    double sd1Lower = vwap - stdDev;        // Values[8] in indicator
                    double sd2Upper = vwap + (2 * stdDev);  // Values[4] in indicator
                    double sd2Lower = vwap - (2 * stdDev);  // Values[10] in indicator

                    // Publish to cache
                    VWAPDataCache.Instance.UpdateVWAP(symbol, vwap, sd1Upper, sd1Lower, sd2Upper, sd2Lower);

                    if (Logger.IsDebugEnabled)
                    {
                        Logger.Debug($"[VWAPCalculatorService] {symbol}: VWAP={vwap:F2}, SD1=[{sd1Lower:F2}-{sd1Upper:F2}], SD2=[{sd2Lower:F2}-{sd2Upper:F2}] (from {includedBars} bars)");
                    }
                }
                else
                {
                    Logger.Debug($"[VWAPCalculatorService] {symbol}: No valid bars for today's session");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[VWAPCalculatorService] CalculateAndPublishVWAP({symbol}): Error - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stops VWAP calculation for an instrument
        /// </summary>
        public void StopVWAPCalculation(string symbol)
        {
            if (_vwapBarsRequests.TryRemove(symbol, out var barsRequest))
            {
                try
                {
                    barsRequest.Dispose();
                    Logger.Info($"[VWAPCalculatorService] Stopped VWAP calculation for {symbol}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[VWAPCalculatorService] Error disposing BarsRequest for {symbol}: {ex.Message}");
                }
            }

            VWAPDataCache.Instance.RemoveVWAP(symbol);
        }

        /// <summary>
        /// Stops all VWAP calculations
        /// </summary>
        public void StopAll()
        {
            Logger.Info($"[VWAPCalculatorService] Stopping all VWAP calculations ({_vwapBarsRequests.Count} active)");

            foreach (var kvp in _vwapBarsRequests)
            {
                try
                {
                    kvp.Value?.Dispose();
                }
                catch { }
            }

            _vwapBarsRequests.Clear();
            VWAPDataCache.Instance.Clear();
        }
    }
}
