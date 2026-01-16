using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Services.Historical;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Historical.Adapters
{
    public interface INT8BarsRequestAdapter
    {
        Task TriggerBarsRequestAsync(string zerodhaSymbol, List<HistoricalCandle> ticks);
    }

    public class NT8BarsRequestAdapter : INT8BarsRequestAdapter
    {
        public NT8BarsRequestAdapter()
        {
            HistoricalTickLogger.Info("[NT8BarsRequestAdapter] Adapter created");
        }

        public async Task TriggerBarsRequestAsync(string zerodhaSymbol, List<HistoricalCandle> candles)
        {
            if (string.IsNullOrEmpty(zerodhaSymbol) || candles == null || candles.Count == 0)
            {
                HistoricalTickLogger.Warn("[NT8BarsRequestAdapter] Invalid parameters - zerodhaSymbol or candles is null/empty");
                return;
            }

            try
            {
                var firstCandle = candles.First();
                var lastCandle = candles.Last();
                HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] START {zerodhaSymbol}: {candles.Count} ticks cached, triggering BarsRequest for range {firstCandle.DateTime.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {lastCandle.DateTime.ToLocalTime():HH:mm:ss}");

                Instrument ntInstrument = null;
                await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
                {
                    ntInstrument = Instrument.GetInstrument(zerodhaSymbol);
                    if (ntInstrument == null)
                    {
                        HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Instrument not found, attempting to create...");
                        var mapping = InstrumentManager.Instance.GetMappingByNtSymbol(zerodhaSymbol);
                        if (mapping != null)
                        {
                            string ntName;
                            NinjaTraderHelper.CreateNTInstrumentFromMapping(mapping, out ntName);
                            ntInstrument = Instrument.GetInstrument(ntName);
                            HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Created instrument, ntName={ntName}");
                        }
                        else
                        {
                            HistoricalTickLogger.Warn($"[NT8BarsRequestAdapter] {zerodhaSymbol}: No mapping found in InstrumentManager");
                        }
                    }
                    else
                    {
                        HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Instrument found - Exchange={ntInstrument.Exchange}");
                    }

                    if (ntInstrument == null)
                    {
                        HistoricalTickLogger.Error($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Could not find or create NT instrument");
                        return;
                    }

                    try
                    {
                        DateTime fromTime = firstCandle.DateTime.ToLocalTime();
                        DateTime toTime = lastCandle.DateTime.ToLocalTime();

                        HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Creating BarsRequest from {fromTime:yyyy-MM-dd HH:mm:ss} to {toTime:HH:mm:ss}");

                        var barsRequest = new BarsRequest(ntInstrument, 100000);
                        barsRequest.BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 };
                        barsRequest.TradingHours = TradingHours.Get("Default 24 x 7");
                        barsRequest.FromLocal = fromTime;
                        barsRequest.ToLocal = toTime;

                        HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Requesting BarsRequest...");

                        barsRequest.Request((barsResult, errorCode, errorMessage) =>
                        {
                            if (errorCode == ErrorCode.NoError)
                            {
                                int barsCount = barsResult?.Bars?.Count ?? 0;
                                HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: BarsRequest callback SUCCESS - BarsWorker served {barsCount} bars from cache");

                                if (barsCount > 0)
                                {
                                    Task.Run(() =>
                                    {
                                        int deleted = TickCacheDb.Instance.DeleteCacheForSymbol(zerodhaSymbol);
                                        HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Cleaned up SQLite cache ({deleted} ticks removed)");
                                    });
                                }
                            }
                            else
                            {
                                HistoricalTickLogger.Warn($"[NT8BarsRequestAdapter] {zerodhaSymbol}: BarsRequest callback FAILED - ErrorCode={errorCode}, Message={errorMessage}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        HistoricalTickLogger.Error($"[NT8BarsRequestAdapter] {zerodhaSymbol}: Exception creating BarsRequest: {ex.Message}", ex);
                    }
                });

                HistoricalTickLogger.Info($"[NT8BarsRequestAdapter] END {zerodhaSymbol}: BarsRequest triggered");
            }
            catch (Exception ex)
            {
                HistoricalTickLogger.Error($"[NT8BarsRequestAdapter] TriggerBarsRequestAsync error for {zerodhaSymbol}: {ex.Message}", ex);
                throw;
            }
        }
    }
}
