using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Historical
{
    /// <summary>
    /// Represents a single historical tick/candle.
    /// Used for both internal caching and NT8 data provision.
    /// </summary>
    public class HistoricalCandle
    {
        public DateTime DateTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long Volume { get; set; }
        public long OpenInterest { get; set; }

        public override string ToString()
        {
            return $"{DateTime:yyyy-MM-dd HH:mm:ss} | O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume}";
        }
    }

    /// <summary>
    /// Request model for queued historical data downloads.
    /// Encapsulates all parameters needed to download option chain history.
    /// </summary>
    public class HistoricalDownloadRequest
    {
        public string Underlying { get; set; }
        public DateTime Expiry { get; set; }
        public int ProjectedAtmStrike { get; set; }
        public List<int> Strikes { get; set; }
        public Dictionary<(int strike, string optionType), string> ZerodhaSymbolMap { get; set; }
        public DateTime? HistoricalDate { get; set; }
        public DateTime QueuedAt { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return $"{Underlying} {Expiry:dd-MMM-yy} ATM={ProjectedAtmStrike} Strikes={Strikes?.Count ?? 0}";
        }
    }

    /// <summary>
    /// Request model for per-instrument tick data download.
    /// Used by BarsWorker when cache misses to queue background download.
    /// </summary>
    public class InstrumentTickDataRequest
    {
        public string ZerodhaSymbol { get; set; }
        public DateTime TradeDate { get; set; }
        public DateTime QueuedAt { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return $"{ZerodhaSymbol} for {TradeDate:yyyy-MM-dd}";
        }
    }

    /// <summary>
    /// State of per-instrument tick data download
    /// </summary>
    public enum TickDataState
    {
        Pending,
        Queued,
        Downloading,
        Ready,
        NoData,
        Failed
    }

    /// <summary>
    /// Status of per-instrument tick data download.
    /// Subscribe to GetInstrumentTickStatusStream() to receive updates.
    /// </summary>
    public class InstrumentTickDataStatus
    {
        public string ZerodhaSymbol { get; set; }
        public TickDataState State { get; set; }
        public DateTime TradeDate { get; set; }
        public int TickCount { get; set; }
        public string ErrorMessage { get; set; }

        public override string ToString()
        {
            return $"{ZerodhaSymbol}: {State} ({TickCount} ticks)";
        }
    }

    /// <summary>
    /// Status of overall historical data service
    /// </summary>
    public enum HistoricalDataState
    {
        NotInitialized,
        WaitingForBroker,
        Ready,
        Downloading,
        Error
    }

    public class HistoricalDataServiceStatus
    {
        public HistoricalDataState State { get; set; }
        public string Message { get; set; }
    }

    public class HistoricalDataDownloadProgress
    {
        public int TotalStrikes { get; set; }
        public int CompletedStrikes { get; set; }
        public List<int> CurrentBatch { get; set; }
        public double PercentComplete { get; set; }
    }

    public class StrikeHistoricalDataStatus
    {
        public string StrikeKey { get; set; }
        public bool IsAvailable { get; set; }
        public int CandleCount { get; set; }
        public DateTime FirstTimestamp { get; set; }
        public DateTime LastTimestamp { get; set; }
        public string ZerodhaSymbol { get; set; }
    }

    public class HistoricalDataResponse
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<HistoricalCandle> Data { get; set; }
    }
}
