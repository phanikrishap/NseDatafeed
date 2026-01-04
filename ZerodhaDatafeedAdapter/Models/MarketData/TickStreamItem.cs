using System;

namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// Immutable tick data item for the reactive stream.
    /// Designed for high-throughput scenarios with minimal allocations.
    ///
    /// Usage with Rx.NET:
    /// - TickStream.Where(t => t.IsOption).Subscribe(HandleOptionTick)
    /// - TickStream.GroupBy(t => t.Symbol).Subscribe(grp => grp.Sample(100ms).Subscribe(...))
    /// - TickStream.Buffer(TimeSpan.FromMilliseconds(100)).Subscribe(HandleBatch)
    /// </summary>
    public readonly struct TickStreamItem
    {
        /// <summary>
        /// The trading symbol (e.g., "NIFTY2610626350CE")
        /// </summary>
        public string Symbol { get; }

        /// <summary>
        /// Zerodha instrument token for this symbol
        /// </summary>
        public int InstrumentToken { get; }

        /// <summary>
        /// Last traded price
        /// </summary>
        public double LastPrice { get; }

        /// <summary>
        /// Last traded quantity
        /// </summary>
        public long LastQuantity { get; }

        /// <summary>
        /// Best bid price
        /// </summary>
        public double BidPrice { get; }

        /// <summary>
        /// Best ask price
        /// </summary>
        public double AskPrice { get; }

        /// <summary>
        /// Total traded volume for the day
        /// </summary>
        public long Volume { get; }

        /// <summary>
        /// Open Interest (for F&O instruments)
        /// </summary>
        public long OpenInterest { get; }

        /// <summary>
        /// Timestamp when tick was received
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// Exchange timestamp from Zerodha
        /// </summary>
        public DateTime ExchangeTimestamp { get; }

        /// <summary>
        /// True if this is an option (CE or PE)
        /// </summary>
        public bool IsOption { get; }

        /// <summary>
        /// True if this is an index (NIFTY, BANKNIFTY, SENSEX, etc.)
        /// </summary>
        public bool IsIndex { get; }

        /// <summary>
        /// Creates a new tick stream item from ZerodhaTickData.
        /// </summary>
        public TickStreamItem(string symbol, ZerodhaTickData tickData, DateTime timestamp)
        {
            Symbol = symbol ?? string.Empty;
            InstrumentToken = tickData?.InstrumentToken ?? 0;
            LastPrice = tickData?.LastTradePrice ?? 0;
            LastQuantity = tickData?.LastTradeQty ?? 0;
            BidPrice = tickData?.BuyPrice ?? 0;
            AskPrice = tickData?.SellPrice ?? 0;
            Volume = tickData?.TotalQtyTraded ?? 0;
            OpenInterest = tickData?.OpenInterest ?? 0;
            Timestamp = timestamp;
            ExchangeTimestamp = tickData?.ExchangeTimestamp ?? DateTime.MinValue;

            // Pre-compute classification flags to avoid repeated string operations
            IsOption = !string.IsNullOrEmpty(symbol) &&
                       (symbol.Contains("CE") || symbol.Contains("PE")) &&
                       !symbol.Contains("SENSEX");

            IsIndex = !string.IsNullOrEmpty(symbol) &&
                      (symbol.StartsWith("NIFTY") ||
                       symbol.StartsWith("BANKNIFTY") ||
                       symbol.StartsWith("SENSEX") ||
                       symbol.StartsWith("FINNIFTY") ||
                       symbol.StartsWith("MIDCPNIFTY")) &&
                      !symbol.Contains("CE") &&
                      !symbol.Contains("PE");
        }

        /// <summary>
        /// Creates a minimal tick stream item (for price-only updates)
        /// </summary>
        public TickStreamItem(string symbol, double lastPrice, DateTime timestamp)
        {
            Symbol = symbol ?? string.Empty;
            InstrumentToken = 0;
            LastPrice = lastPrice;
            LastQuantity = 0;
            BidPrice = 0;
            AskPrice = 0;
            Volume = 0;
            OpenInterest = 0;
            Timestamp = timestamp;
            ExchangeTimestamp = DateTime.MinValue;

            IsOption = !string.IsNullOrEmpty(symbol) &&
                       (symbol.Contains("CE") || symbol.Contains("PE")) &&
                       !symbol.Contains("SENSEX");

            IsIndex = !string.IsNullOrEmpty(symbol) &&
                      (symbol.StartsWith("NIFTY") ||
                       symbol.StartsWith("BANKNIFTY") ||
                       symbol.StartsWith("SENSEX") ||
                       symbol.StartsWith("FINNIFTY") ||
                       symbol.StartsWith("MIDCPNIFTY")) &&
                      !symbol.Contains("CE") &&
                      !symbol.Contains("PE");
        }

        public override string ToString()
        {
            return $"{Symbol}: {LastPrice:F2} @ {Timestamp:HH:mm:ss.fff}";
        }
    }
}
