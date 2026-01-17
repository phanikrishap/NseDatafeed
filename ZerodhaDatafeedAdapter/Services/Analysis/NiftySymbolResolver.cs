using System;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Component responsible for resolving NIFTY Futures symbols and NinjaTrader Instruments.
    /// </summary>
    public class NiftySymbolResolver
    {
        public NiftySymbolResolver()
        {
        }

        /// <summary>
        /// Resolves the current NIFTY Futures contract symbol from SQLite database.
        /// Pattern: NIFTY{YY}{MMM}FUT (e.g., NIFTY26JANFUT)
        /// </summary>
        public async Task<string> ResolveNiftyFuturesSymbolAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Query SQLite for NFO-FUT segment, NIFTY underlying, nearest expiry
                    var (token, tradingSymbol, _) = InstrumentManager.Instance.LookupFuturesInSqlite(
                        "NFO-FUT",
                        "NIFTY",
                        DateTime.Today);

                    if (token > 0 && !string.IsNullOrEmpty(tradingSymbol))
                    {
                        Logger.Info($"[NiftySymbolResolver] Found {tradingSymbol} (token={token})");
                        return tradingSymbol;
                    }

                    Logger.Warn("[NiftySymbolResolver] No futures contract found");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[NiftySymbolResolver] Exception - {ex.Message}", ex);
                    return null;
                }
            });
        }

        /// <summary>
        /// Gets the NinjaTrader Instrument handle for a given symbol.
        /// </summary>
        public async Task<Instrument> GetInstrumentAsync(string symbol, bool isRetry = false)
        {
            return await NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(() =>
            {
                var instrument = Instrument.GetInstrument(symbol);
                if (instrument == null)
                {
                    if (isRetry)
                        Logger.Warn($"[NiftySymbolResolver] Instrument not found for {symbol}");
                }
                else
                {
                    Logger.Info($"[NiftySymbolResolver] Got instrument for {symbol}");
                }
                return instrument;
            });
        }
    }
}
