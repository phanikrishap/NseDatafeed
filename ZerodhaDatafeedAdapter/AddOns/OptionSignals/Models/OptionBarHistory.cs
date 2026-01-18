using System;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    /// <summary>
    /// Represents a single bar's state snapshot for an option.
    /// Stores all indicator values at that specific bar time.
    /// </summary>
    public struct OptionBarSnapshot
    {
        public DateTime BarTime;
        public double ClosePrice;

        // VP Session metrics
        public int SessHvnB;
        public int SessHvnS;
        public HvnTrend SessTrend;

        // VP Rolling metrics
        public int RollHvnB;
        public int RollHvnS;
        public HvnTrend RollTrend;

        // Momentum metrics
        public double CDMomentum;
        public double CDSmooth;
        public MomentumBias CDBias;
        public double PriceMomentum;
        public double PriceSmooth;
        public MomentumBias PriceBias;

        // VWAP metrics
        public int VwapScoreSess;
        public int VwapScoreRoll;
        public double SessionVWAP;
        public double RollingVWAP;

        // Cumulative Delta (raw value)
        public long CumulativeDelta;

        // Session SD Bands
        public double SessStdDev;
        public double SessUpper1SD;
        public double SessUpper2SD;
        public double SessLower1SD;
        public double SessLower2SD;

        // Rolling SD Bands
        public double RollStdDev;
        public double RollUpper1SD;
        public double RollUpper2SD;
        public double RollLower1SD;
        public double RollLower2SD;
    }

    /// <summary>
    /// Circular buffer storing historical bar snapshots for an option symbol.
    /// Allows lookup of indicator values at any historical bar time within the buffer.
    /// </summary>
    public class OptionBarHistory
    {
        private readonly OptionBarSnapshot[] _buffer;
        private readonly int _capacity;
        private int _head; // Next write position
        private int _count; // Number of valid entries

        public string Symbol { get; }
        public double Strike { get; }
        public string OptionType { get; } // CE or PE

        /// <summary>
        /// Creates a new circular buffer with specified capacity.
        /// </summary>
        /// <param name="symbol">Option symbol</param>
        /// <param name="strike">Strike price</param>
        /// <param name="optionType">CE or PE</param>
        /// <param name="capacity">Number of bars to store (default 256)</param>
        public OptionBarHistory(string symbol, double strike, string optionType, int capacity = 256)
        {
            Symbol = symbol;
            Strike = strike;
            OptionType = optionType;
            _capacity = capacity;
            _buffer = new OptionBarSnapshot[capacity];
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Adds a new bar snapshot to the buffer.
        /// </summary>
        public void AddBar(OptionBarSnapshot snapshot)
        {
            _buffer[_head] = snapshot;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        /// <summary>
        /// Gets the most recent bar snapshot.
        /// </summary>
        public OptionBarSnapshot? GetLatest()
        {
            if (_count == 0) return null;
            int latestIdx = (_head - 1 + _capacity) % _capacity;
            return _buffer[latestIdx];
        }

        /// <summary>
        /// Gets the bar snapshot at or just before the specified time.
        /// Returns the closest bar that is <= targetTime.
        /// </summary>
        public OptionBarSnapshot? GetAtOrBefore(DateTime targetTime)
        {
            if (_count == 0) return null;

            OptionBarSnapshot? best = null;

            // Scan from oldest to newest
            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _capacity;
                if (_buffer[idx].BarTime <= targetTime)
                {
                    best = _buffer[idx];
                }
                else
                {
                    // Past target time, stop scanning
                    break;
                }
            }

            return best;
        }

        /// <summary>
        /// Gets the bar snapshot at the exact specified time.
        /// </summary>
        public OptionBarSnapshot? GetAtTime(DateTime targetTime)
        {
            if (_count == 0) return null;

            int start = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _capacity;
                if (_buffer[idx].BarTime == targetTime)
                {
                    return _buffer[idx];
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the close price at or just before the specified time.
        /// </summary>
        public double? GetPriceAtOrBefore(DateTime targetTime)
        {
            var snapshot = GetAtOrBefore(targetTime);
            return snapshot?.ClosePrice;
        }

        /// <summary>
        /// Number of bars currently stored.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Maximum capacity of the buffer.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Clears all stored bars.
        /// </summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
