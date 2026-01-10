using System;
using ZerodhaDatafeedAdapter.Services.Analysis.Components;

namespace ZerodhaDatafeedAdapter.AddOns.OptionSignals.Models
{
    /// <summary>
    /// Represents a single bar's state snapshot for the underlying (Nifty Futures).
    /// Stores all VP indicator values at that specific bar time.
    /// </summary>
    public struct UnderlyingBarSnapshot
    {
        public DateTime BarTime;
        public double ClosePrice;

        // Session VP metrics
        public double POC;
        public double VAH;
        public double VAL;
        public double VWAP;
        public int SessHvnB;
        public int SessHvnS;
        public HvnTrend SessTrend;

        // Rolling VP metrics (60-min window)
        public double RollingPOC;
        public double RollingVAH;
        public double RollingVAL;
        public int RollHvnB;
        public int RollHvnS;
        public HvnTrend RollTrend;

        // Relative metrics
        public double RelHvnBuySess;
        public double RelHvnSellSess;
        public double RelHvnBuyRoll;
        public double RelHvnSellRoll;
    }

    /// <summary>
    /// Circular buffer storing historical bar snapshots for the underlying (Nifty Futures).
    /// Allows lookup of VP indicator values at any historical bar time within the buffer.
    /// </summary>
    public class UnderlyingBarHistory
    {
        private readonly UnderlyingBarSnapshot[] _buffer;
        private readonly int _capacity;
        private int _head; // Next write position
        private int _count; // Number of valid entries

        public string Symbol { get; }

        /// <summary>
        /// Creates a new circular buffer with specified capacity.
        /// </summary>
        /// <param name="symbol">Underlying symbol (e.g., NIFTY_I)</param>
        /// <param name="capacity">Number of bars to store (default 256)</param>
        public UnderlyingBarHistory(string symbol, int capacity = 256)
        {
            Symbol = symbol;
            _capacity = capacity;
            _buffer = new UnderlyingBarSnapshot[capacity];
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Adds a new bar snapshot to the buffer.
        /// </summary>
        public void AddBar(UnderlyingBarSnapshot snapshot)
        {
            _buffer[_head] = snapshot;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        /// <summary>
        /// Gets the most recent bar snapshot.
        /// </summary>
        public UnderlyingBarSnapshot? GetLatest()
        {
            if (_count == 0) return null;
            int latestIdx = (_head - 1 + _capacity) % _capacity;
            return _buffer[latestIdx];
        }

        /// <summary>
        /// Gets the bar snapshot at or just before the specified time.
        /// Returns the closest bar that is <= targetTime.
        /// </summary>
        public UnderlyingBarSnapshot? GetAtOrBefore(DateTime targetTime)
        {
            if (_count == 0) return null;

            UnderlyingBarSnapshot? best = null;

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
        public UnderlyingBarSnapshot? GetAtTime(DateTime targetTime)
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
