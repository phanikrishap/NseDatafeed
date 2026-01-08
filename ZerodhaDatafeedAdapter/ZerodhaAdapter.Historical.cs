using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ZerodhaAdapterAddOn.ViewModels;
using ZerodhaAPI.Common.Enums;
using ZerodhaDatafeedAdapter.Classes;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.SyntheticInstruments;
using ZerodhaDatafeedAdapter.ViewModels;

#nullable disable
namespace ZerodhaDatafeedAdapter
{
    /// <summary>
    /// ZerodhaAdapter partial class - Historical Data Requests
    /// Handles BarsWorker and RequestBars for historical data loading
    /// </summary>
    public partial class ZerodhaAdapter
    {
        private int HowManyBarsFromDays(DateTime startDate) => (DateTime.Now - startDate).Days;

        private int HowManyBarsFromMinutes(DateTime startDate)
        {
            return Convert.ToInt32((DateTime.Now - startDate).TotalMinutes);
        }

        public void RequestBars(
            IBars bars,
            Action<IBars, ErrorCode, string> callback,
            IProgress progress)
        {
            try
            {
                BarsRequest request = new BarsRequest()
                {
                    Bars = bars,
                    BarsCallback = callback,
                    Progress = progress
                };
                Task.Run((Action)(() => this.BarsWorker(request)));
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        private void BarsWorker(BarsRequest barsRequest)
        {
            if (this._zerodhaConncetion.Trace.Bars)
                this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.BarsWorker"));

            EventHandler eventHandler = (EventHandler)((s, e) => { });

            try
            {
                string rawName = barsRequest?.Bars?.Instrument?.MasterInstrument?.Name ?? "NULL";
                var barsPeriodType = barsRequest?.Bars?.BarsPeriod?.BarsPeriodType;
                Logger.Info($"[BarsWorker] ========== BARS REQUEST ==========");
                Logger.Info($"[BarsWorker] Incoming symbol: '{rawName}', BarsPeriodType: {barsPeriodType}");

                if (barsRequest.Progress != null)
                {
                    string shortDatePattern = Globals.GeneralOptions.CurrentCulture.DateTimeFormat.ShortDatePattern;
                    CultureInfo currentCulture = Globals.GeneralOptions.CurrentCulture;
                    barsRequest.Progress.Aborted += eventHandler;
                }

                bool flag = false;
                string name = barsRequest.Bars.Instrument.MasterInstrument.Name;
                MarketType marketType = MarketType.Spot;
                string symbolName = Connector.GetSymbolName(name, out marketType);

                Logger.Info($"[BarsWorker] Resolved: '{name}' -> '{symbolName}' (MarketType={marketType})");

                // Create the loading UI
                LoadViewModel loadViewModel = new LoadViewModel();
                loadViewModel.Message = "Loading historical data...";
                loadViewModel.SubMessage = "Preparing request";
                loadViewModel.IsBusy = true;

                List<Record> source = null;

                try
                {
                    Task<List<Record>> task = null;

                    DateTime fromDateWithTime = new DateTime(
                        barsRequest.Bars.FromDate.Year,
                        barsRequest.Bars.FromDate.Month,
                        barsRequest.Bars.FromDate.Day,
                        9, 15, 0);  // 9:15:00 AM IST
                    DateTime toDateWithTime = new DateTime(
                           barsRequest.Bars.ToDate.Year,
                           barsRequest.Bars.ToDate.Month,
                           barsRequest.Bars.ToDate.Day,
                           15, 30, 0);  // 3:30:00 PM IST

                    // Check if this is a synthetic straddle symbol
                    if (SyntheticHistoricalDataService.IsSyntheticSymbol(name))
                    {
                        task = ProcessSyntheticHistoricalRequest(name, barsRequest, fromDateWithTime, toDateWithTime, loadViewModel, out source);
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Day && this.HowManyBarsFromDays(barsRequest.Bars.FromDate) > 0)
                    {
                        task = Connector.Instance.GetHistoricalTrades(barsRequest.Bars.BarsPeriod.BarsPeriodType, symbolName, fromDateWithTime, toDateWithTime, marketType, (ViewModelBase)loadViewModel);
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && this.HowManyBarsFromMinutes(barsRequest.Bars.FromDate) > 0)
                    {
                        task = Connector.Instance.GetHistoricalTrades(barsRequest.Bars.BarsPeriod.BarsPeriodType, symbolName, fromDateWithTime, toDateWithTime, marketType, (ViewModelBase)loadViewModel);
                    }
                    else if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Tick)
                    {
                        // Tick data not available from Zerodha
                        source = new List<Record>();
                        Logger.Info("Historical tick data not available from Zerodha. Using empty history with real-time tick subscription.");
                    }

                    if (task != null)
                    {
                        try
                        {
                            source = task.Result;
                            Logger.Debug($"Retrieved {source?.Count ?? 0} historical data points");
                        }
                        catch (AggregateException ae)
                        {
                            string errorMsg = ae.InnerException?.Message ?? ae.Message;
                            NinjaTrader.NinjaScript.NinjaScript.Log($"Error retrieving historical data: {errorMsg}", NinjaTrader.Cbi.LogLevel.Error);
                            flag = true;
                        }
                    }
                    else if (source == null)
                    {
                        Logger.Info("No historical data request was made");
                    }
                }
                finally
                {
                    loadViewModel.IsBusy = false;
                    loadViewModel.Message = "";
                    loadViewModel.SubMessage = "";
                }

                // Process the data if available
                if (source != null)
                {
                    if (source.Count == 0)
                    {
                        Logger.Info("No data returned from historical data request");
                    }

                    foreach (Record record in source)
                    {
                        if (barsRequest.Progress != null && barsRequest.Progress.IsAborted)
                        {
                            flag = true;
                            break;
                        }

                        if (this._zerodhaConncetion.Status != ConnectionStatus.Disconnecting)
                        {
                            if (this._zerodhaConncetion.Status != ConnectionStatus.Disconnected)
                            {
                                double open = record.Open;
                                double high = record.High;
                                double low = record.Low;
                                double close = record.Close;

                                if (record.Volume >= 0.0)
                                {
                                    long volume = (long)record.Volume;

                                    TimeZoneInfo indianZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.IndianTimeZoneId);
                                    DateTime displayTime = TimeZoneInfo.ConvertTime(record.TimeStamp, indianZone);

                                    if (displayTime >= barsRequest.Bars.FromDate)
                                    {
                                        TimeSpan timeOfDay = displayTime.TimeOfDay;

                                        if (timeOfDay >= Constants.MarketOpenTime && timeOfDay <= Constants.MarketCloseTime)
                                        {
                                            barsRequest.Bars.Add(open, high, low, close, displayTime, volume, double.MinValue, double.MinValue);
                                        }
                                    }
                                }
                            }
                            else
                                break;
                        }
                        else
                            break;
                    }
                }

                if (barsRequest == null)
                    return;

                if (barsRequest.Progress != null)
                {
                    barsRequest.Progress.Aborted -= eventHandler;
                    barsRequest.Progress.TearDown();
                }

                IBars bars = barsRequest.Bars;
                barsRequest.BarsCallback(bars, flag ? ErrorCode.UserAbort : ErrorCode.NoError, string.Empty);
                barsRequest = null;
            }
            catch (Exception ex)
            {
                string errorMessage = $"BarsWorker Exception: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $" Inner: {ex.InnerException.Message}";
                }

                NinjaTrader.NinjaScript.NinjaScript.Log(errorMessage, NinjaTrader.Cbi.LogLevel.Error);
                NinjaTrader.NinjaScript.NinjaScript.Log($"Stack trace: {ex.StackTrace}", NinjaTrader.Cbi.LogLevel.Error);

                if (this._zerodhaConncetion.Trace.Bars)
                    this._zerodhaConncetion.TraceCallback(string.Format((IFormatProvider)CultureInfo.InvariantCulture, $"({this._options.Name}) ZerodhaAdapter.BarsWorker Exception='{ex.ToString()}'"));

                if (barsRequest == null)
                    return;

                if (barsRequest.Progress != null)
                {
                    barsRequest.Progress.Aborted -= eventHandler;
                    barsRequest.Progress.TearDown();
                }

                IBars bars = barsRequest.Bars;
                barsRequest.BarsCallback(bars, ErrorCode.Panic, errorMessage);
            }
        }

        private Task<List<Record>> ProcessSyntheticHistoricalRequest(
            string name,
            BarsRequest barsRequest,
            DateTime fromDateWithTime,
            DateTime toDateWithTime,
            LoadViewModel loadViewModel,
            out List<Record> source)
        {
            source = null;
            Task<List<Record>> task = null;

            var straddleDef = _syntheticStraddleService?.GetDefinition(name);
            if (straddleDef != null)
            {
                Logger.Debug($"[SYNTH-HIST] Processing synthetic straddle: {name} (CE={straddleDef.CESymbol}, PE={straddleDef.PESymbol})");

                if (barsRequest.Bars.BarsPeriod.BarsPeriodType == BarsPeriodType.Tick)
                {
                    source = new List<Record>();
                    Logger.Info("[SYNTH-HIST] Tick data not supported for synthetic straddles. Using empty history with real-time tick subscription.");
                }
                else
                {
                    task = SyntheticHistoricalDataService.Instance.GetSyntheticHistoricalData(
                        barsRequest.Bars.BarsPeriod.BarsPeriodType,
                        name,
                        straddleDef.CESymbol,
                        straddleDef.PESymbol,
                        fromDateWithTime,
                        toDateWithTime,
                        (ViewModelBase)loadViewModel);
                }
            }
            else
            {
                Logger.Warn($"[SYNTH-HIST] No straddle definition found for {name}. Returning empty data.");
                source = new List<Record>();
            }

            return task;
        }

        private bool IsIndianMarketInstrument(Instrument instrument)
        {
            string name = instrument.MasterInstrument.Name;
            return name.EndsWith("-NSE") || name.EndsWith("-BSE") ||
                   instrument.Exchange == Exchange.Nse || instrument.Exchange == Exchange.Bse;
        }

        private class BarsRequest
        {
            public IBars Bars { get; set; }
            public Action<IBars, ErrorCode, string> BarsCallback { get; set; }
            public IProgress Progress { get; set; }
        }
    }
}
