using System;
using ZerodhaAPI.Common.Enums;

namespace ZerodhaDatafeedAdapter.Models
{
    /// <summary>
    /// Represents a broker-agnostic instrument definition used within the adapter.
    /// Replaces misappropriated Binance-specific models.
    /// </summary>
    public class InstrumentDefinition
    {
        /// <summary>
        /// The primary trading symbol (e.g., "NIFTY_I", "RELIANCE").
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// The native symbol used by the broker (e.g., Zerodha's tradingsymbol).
        /// </summary>
        public string BrokerSymbol { get; set; }

        /// <summary>
        /// The segment or exchange name (e.g., "NSE", "NFO", "MCX").
        /// </summary>
        public string Segment { get; set; }

        /// <summary>
        /// The unique instrument token assigned by the broker.
        /// </summary>
        public long InstrumentToken { get; set; }

        /// <summary>
        /// The exchange-specific token, if applicable.
        /// </summary>
        public long? ExchangeToken { get; set; }

        /// <summary>
        /// The type of instrument (e.g., Stock, Future, Option).
        /// </summary>
        public MarketType MarketType { get; set; }

        /// <summary>
        /// The minimum price increment for the instrument.
        /// </summary>
        public double TickSize { get; set; }

        /// <summary>
        /// The minimum number of units per trade.
        /// </summary>
        public int LotSize { get; set; }

        /// <summary>
        /// The expiry date for futures/options, if applicable.
        /// </summary>
        public DateTime? Expiry { get; set; }

        /// <summary>
        /// The strike price for options, if applicable.
        /// </summary>
        public double? Strike { get; set; }

        /// <summary>
        /// The underlying asset for derivatives.
        /// </summary>
        public string Underlying { get; set; }

        /// <summary>
        /// The base asset of the pair (e.g., "BTC" in "BTCUSDT").
        /// </summary>
        public string BaseAsset { get; set; }

        /// <summary>
        /// The quote asset of the pair (e.g., "USDT" in "BTCUSDT").
        /// </summary>
        public string QuoteAsset { get; set; }

        /// <summary>
        /// Indicates if the instrument is currently available for trading.
        /// </summary>
        public bool IsActive { get; set; } = true;
    }
}
