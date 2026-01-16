using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ZerodhaDatafeedAdapter.Logging;
using ZerodhaDatafeedAdapter.Models;
using ZerodhaDatafeedAdapter.Models.Reactive;
using ZerodhaDatafeedAdapter.Services.Instruments;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Service responsible for managing Ticker instances (NIFTY, SENSEX, GIFT NIFTY)
    /// and routing price updates.
    /// Extracted from MarketAnalyzerLogic for modularity.
    /// </summary>
    public class TickerService
    {
        private static readonly Lazy<TickerService> _instance = new Lazy<TickerService>(() => new TickerService());
        public static TickerService Instance => _instance.Value;

        // Static ticker instances
        public TickerData GiftNiftyTicker { get; } = new TickerData { Symbol = "GIFT NIFTY" };
        public TickerData NiftyTicker { get; } = new TickerData { Symbol = "NIFTY 50" };
        public TickerData SensexTicker { get; } = new TickerData { Symbol = "SENSEX" };
        public TickerData NiftyFuturesTicker { get; } = new TickerData { Symbol = "NIFTY_I" };

        public ObservableCollection<TickerData> Tickers { get; } = new ObservableCollection<TickerData>();

        // Property Accessors
        public double GiftNiftyPrice => GiftNiftyTicker.CurrentPrice;
        public double NiftySpotPrice => NiftyTicker.CurrentPrice;
        public double SensexSpotPrice => SensexTicker.CurrentPrice;
        public double NiftyFuturesPrice => NiftyFuturesTicker.CurrentPrice;
        
        public double GiftNiftyPriorClose { get; private set; }

        // Alias sets
        public static readonly HashSet<string> GiftNiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GIFT_NIFTY", "GIFT NIFTY", "GIFTNIFTY" };
        public static readonly HashSet<string> NiftyAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "NIFTY", "NIFTY 50", "NIFTY_50", "NIFTY50", "NIFTY_SPOT" };
        public static readonly HashSet<string> SensexAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SENSEX", "SENSEX_SPOT", "BSE:SENSEX" };
        
        // NIFTY Futures management
        private HashSet<string> _niftyFuturesAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NIFTY_I" };
        public string NiftyFuturesSymbol { get; private set; }
        public long NiftyFuturesToken { get; private set; }
        public DateTime NiftyFuturesExpiry { get; private set; }

        // Reactive Streams
        private readonly Subject<TickerPriceUpdate> _priceUpdateSubject = new Subject<TickerPriceUpdate>();
        public IObservable<TickerPriceUpdate> PriceUpdateStream => _priceUpdateSubject.AsObservable();

        public event Action<string> TickerUpdated;

        private TickerService()
        {
            Tickers.Add(GiftNiftyTicker);
            Tickers.Add(NiftyTicker);
            Tickers.Add(SensexTicker);
            Tickers.Add(NiftyFuturesTicker);

            SubscribeToInstrumentDbReadyForFuturesResolution();
        }

        private void SubscribeToInstrumentDbReadyForFuturesResolution()
        {
            MarketDataReactiveHub.Instance.InstrumentDbReadyStream
                .Where(ready => ready)
                .Timeout(TimeSpan.FromSeconds(90))
                .Take(1)
                .Subscribe(
                    _ =>
                    {
                        Logger.Info("[TickerService] InstrumentDbReady received - resolving NIFTY Futures contract");
                        ResolveNiftyFuturesContract();
                    },
                    ex => Logger.Error($"[TickerService] Error awaiting InstrumentDbReady: {ex.Message}"));
        }

        private void ResolveNiftyFuturesContract()
        {
            var result = InstrumentManager.Instance.LookupFuturesInSqlite("NFO-FUT", "NIFTY", DateTime.Today);

            if (result.token > 0)
            {
                NiftyFuturesToken = result.token;
                NiftyFuturesSymbol = result.symbol;
                NiftyFuturesExpiry = result.expiry;

                lock (_niftyFuturesAliases)
                {
                    _niftyFuturesAliases.Add(NiftyFuturesSymbol);
                }

                Logger.Info($"[TickerService] ResolveNiftyFuturesContract(): Resolved NIFTY_I -> {NiftyFuturesSymbol} (token={NiftyFuturesToken}, expiry={NiftyFuturesExpiry:yyyy-MM-dd})");
            }
            else
            {
                Logger.Warn("[TickerService] ResolveNiftyFuturesContract(): No NIFTY futures contract found");
            }
        }

        public bool IsNiftyFuturesAlias(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return false;
            if (_niftyFuturesAliases.Contains(symbol)) return true;
            return symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) &&
                   symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsGiftNifty(string symbol) => GiftNiftyAliases.Contains(symbol);

        public void UpdatePrice(string symbol, double price, double priorClose = 0)
        {
            TickerData ticker = null;

            if (GiftNiftyAliases.Contains(symbol))
            {
                ticker = GiftNiftyTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0)
                {
                    GiftNiftyPriorClose = priorClose;
                    ticker.Close = priorClose;
                }
            }
            else if (NiftyAliases.Contains(symbol))
            {
                ticker = NiftyTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0) ticker.Close = priorClose;
            }
            else if (SensexAliases.Contains(symbol))
            {
                ticker = SensexTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0) ticker.Close = priorClose;
            }
            else if (IsNiftyFuturesAlias(symbol))
            {
                ticker = NiftyFuturesTicker;
                ticker.UpdatePrice(price);
                if (priorClose > 0) ticker.Close = priorClose;
            }

            if (ticker != null)
            {
                TickerUpdated?.Invoke(ticker.Symbol);

                _priceUpdateSubject.OnNext(new TickerPriceUpdate
                {
                    TickerSymbol = ticker.Symbol,
                    Price = ticker.CurrentPrice,
                    Close = ticker.Close,
                    NetChangePercent = ticker.NetChangePercent
                });
            }
        }

        public void UpdateClose(string symbol, double closePrice)
        {
            TickerData ticker = null;

            if (GiftNiftyAliases.Contains(symbol))
            {
                GiftNiftyPriorClose = closePrice;
                ticker = GiftNiftyTicker;
                ticker.Close = closePrice;
            }
            else if (NiftyAliases.Contains(symbol))
            {
                ticker = NiftyTicker;
                ticker.Close = closePrice;
            }
            else if (SensexAliases.Contains(symbol))
            {
                ticker = SensexTicker;
                ticker.Close = closePrice;
            }
            else if (IsNiftyFuturesAlias(symbol))
            {
                ticker = NiftyFuturesTicker;
                ticker.Close = closePrice;
            }

            if (ticker != null)
            {
                _priceUpdateSubject.OnNext(new TickerPriceUpdate
                {
                    TickerSymbol = ticker.Symbol,
                    Price = ticker.CurrentPrice,
                    Close = ticker.Close,
                    NetChangePercent = ticker.NetChangePercent
                });
            }
        }

        public void Reset()
        {
            ResetTicker(GiftNiftyTicker);
            ResetTicker(NiftyTicker);
            ResetTicker(SensexTicker);
            ResetTicker(NiftyFuturesTicker);
            
            GiftNiftyPriorClose = 0;
            // Note: NiftyFutures resolution is not cleared as that's DB dependent and stable for the day
        }

        private void ResetTicker(TickerData ticker)
        {
            ticker.CurrentPrice = 0;
            ticker.Open = 0;
            ticker.High = 0;
            ticker.Low = 0;
            ticker.Close = 0;
            ticker.ProjectedOpen = 0;
        }
    }
}
