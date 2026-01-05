using System;

namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// Per-symbol state owned exclusively by a shard worker.
    /// No locks needed - single-writer guarantee within a shard.
    /// </summary>
    public class SymbolState
    {
        public int PreviousVolume { get; set; }
        public double PreviousPrice { get; set; }
        public DateTime LastTickTime { get; set; }
    }
}
