using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Result of Cumulative Delta calculation for a bar.
    /// </summary>
    public class CumulativeDeltaResult
    {
        /// <summary>
        /// Bar delta (close) - net buy/sell volume for this bar.
        /// Positive = more buying, Negative = more selling.
        /// </summary>
        public long BarDelta { get; set; }

        /// <summary>
        /// Maximum delta reached during this bar.
        /// </summary>
        public long MaxDelta { get; set; }

        /// <summary>
        /// Minimum delta reached during this bar.
        /// </summary>
        public long MinDelta { get; set; }

        /// <summary>
        /// Cumulative delta close - running total of all bar deltas.
        /// </summary>
        public long CumulativeDeltaClose { get; set; }

        /// <summary>
        /// Cumulative delta high - highest cumulative value during this bar.
        /// </summary>
        public long CumulativeDeltaHigh { get; set; }

        /// <summary>
        /// Cumulative delta low - lowest cumulative value during this bar.
        /// </summary>
        public long CumulativeDeltaLow { get; set; }

        /// <summary>
        /// Bar timestamp.
        /// </summary>
        public DateTime Time { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Cumulative Delta Engine - Accumulates delta continuously (does NOT reset on session).
    /// Matches smaCumulativeDelta behavior: CDClose[0] = CDClose[1] + BarDelta.
    /// Tracks bar-by-bar delta and cumulative totals.
    /// Uses UpDownTick method: price >= lastPrice = Buy, price < lastPrice = Sell.
    /// </summary>
    public class CumulativeDeltaEngine
    {
        private long _runningCumulativeDelta = 0;
        private long _barDelta = 0;
        private long _barMaxDelta = 0;
        private long _barMinDelta = 0;
        private double _lastPrice = 0;
        private bool _isFirstBar = true;

        /// <summary>
        /// Resets the engine completely (use only when starting fresh, not on new session).
        /// </summary>
        public void Reset()
        {
            _runningCumulativeDelta = 0;
            _barDelta = 0;
            _barMaxDelta = 0;
            _barMinDelta = 0;
            _lastPrice = 0;
            _isFirstBar = true;
        }

        /// <summary>
        /// Starts a new bar - call before processing ticks for a new bar.
        /// Does NOT reset on new session (matches smaCumulativeDelta behavior).
        /// </summary>
        public void StartNewBar()
        {
            // Reset bar-level accumulators only (NOT cumulative total)
            _barDelta = 0;
            _barMaxDelta = 0;
            _barMinDelta = 0;
        }

        /// <summary>
        /// Adds a tick to the current bar.
        /// Delta is computed using UpDownTick: price >= lastPrice = Buy (+volume), else Sell (-volume).
        /// </summary>
        public void AddTick(double price, long volume, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            // UpDownTick delta calculation
            long tickDelta = (price >= _lastPrice) ? volume : -volume;

            // First tick uses its own price as reference (neutral)
            if (_lastPrice == 0)
                tickDelta = 0;

            _barDelta += tickDelta;
            _barMaxDelta = Math.Max(_barMaxDelta, _barDelta);
            _barMinDelta = Math.Min(_barMinDelta, _barDelta);

            _lastPrice = price;
        }

        /// <summary>
        /// Adds a tick with pre-computed isBuy flag (used when delta is already determined).
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            long tickDelta = isBuy ? volume : -volume;
            _barDelta += tickDelta;
            _barMaxDelta = Math.Max(_barMaxDelta, _barDelta);
            _barMinDelta = Math.Min(_barMinDelta, _barDelta);

            _lastPrice = price;
        }

        /// <summary>
        /// Adds a tick with explicit buy/sell volume split.
        /// Used for NinjaTrader-style 50/50 split when price equals prior price.
        /// Delta = buyVolume - sellVolume
        /// </summary>
        public void AddTickSplit(double price, long totalVolume, long buyVolume, long sellVolume, DateTime tickTime)
        {
            if (price <= 0 || totalVolume <= 0) return;

            long tickDelta = buyVolume - sellVolume;
            _barDelta += tickDelta;
            _barMaxDelta = Math.Max(_barMaxDelta, _barDelta);
            _barMinDelta = Math.Min(_barMinDelta, _barDelta);

            _lastPrice = price;
        }

        /// <summary>
        /// Closes the current bar and returns cumulative delta result.
        /// Call after all ticks for the bar have been processed.
        /// Matches smaCumulativeDelta: if CurrentBar == 0: CDClose = 0, else CDClose = CDClose[1] + BarDelta
        /// </summary>
        public CumulativeDeltaResult CloseBar(DateTime barTime)
        {
            var result = new CumulativeDeltaResult();
            result.Time = barTime;

            if (_isFirstBar)
            {
                // First bar: CDClose = BarDelta (session starts with first bar's delta, not 0)
                // This differs from smaCumulativeDelta (which returns 0 for CurrentBar == 0)
                // but provides more meaningful values for session-based analysis
                result.BarDelta = _barDelta;
                result.MaxDelta = _barMaxDelta;
                result.MinDelta = _barMinDelta;
                result.CumulativeDeltaClose = _barDelta;  // Use bar delta instead of 0
                result.CumulativeDeltaHigh = _barMaxDelta;
                result.CumulativeDeltaLow = _barMinDelta;
                result.IsValid = true;
                _runningCumulativeDelta = _barDelta;  // Initialize running total with first bar's delta
                _isFirstBar = false;
            }
            else
            {
                // Subsequent bars: CDClose = CDClose[1] + BarDelta
                long prevCumDelta = _runningCumulativeDelta;
                _runningCumulativeDelta += _barDelta;

                result.BarDelta = _barDelta;
                result.MaxDelta = _barMaxDelta;
                result.MinDelta = _barMinDelta;
                result.CumulativeDeltaClose = _runningCumulativeDelta;
                result.CumulativeDeltaHigh = prevCumDelta + _barMaxDelta;
                result.CumulativeDeltaLow = prevCumDelta + _barMinDelta;
                result.IsValid = true;
            }

            return result;
        }

        /// <summary>
        /// Gets the current running cumulative delta (without closing the bar).
        /// </summary>
        public long CurrentCumulativeDelta => _runningCumulativeDelta + _barDelta;

        /// <summary>
        /// Gets the current bar delta (without closing the bar).
        /// </summary>
        public long CurrentBarDelta => _barDelta;

        public CumulativeDeltaEngine Clone()
        {
            return (CumulativeDeltaEngine)this.MemberwiseClone();
        }

        public void Restore(CumulativeDeltaEngine other)
        {
            if (other == null) return;
            this._runningCumulativeDelta = other._runningCumulativeDelta;
            this._barDelta = other._barDelta;
            this._barMaxDelta = other._barMaxDelta;
            this._barMinDelta = other._barMinDelta;
            this._lastPrice = other._lastPrice;
            this._isFirstBar = other._isFirstBar;
        }
    }

    /// <summary>
    /// Rolling Cumulative Delta Engine - Maintains a time-windowed cumulative delta.
    /// Uses a rolling window (default 60 minutes) with automatic expiration of old data.
    /// </summary>
    public class RollingCumulativeDeltaEngine
    {
        private readonly Queue<DeltaBarRecord> _barRecords = new Queue<DeltaBarRecord>();
        private int _rollingWindowMinutes = 60;
        private long _rollingCumulativeDelta = 0;

        // Current bar accumulators
        private long _barDelta = 0;
        private long _barMaxDelta = 0;
        private long _barMinDelta = 0;
        private double _lastPrice = 0;

        private class DeltaBarRecord
        {
            public DateTime Time { get; set; }
            public long BarDelta { get; set; }
        }

        public RollingCumulativeDeltaEngine(int rollingWindowMinutes = 60)
        {
            _rollingWindowMinutes = rollingWindowMinutes;
        }

        /// <summary>
        /// Resets the engine completely.
        /// </summary>
        public void Reset()
        {
            _barRecords.Clear();
            _rollingCumulativeDelta = 0;
            _barDelta = 0;
            _barMaxDelta = 0;
            _barMinDelta = 0;
            _lastPrice = 0;
        }

        /// <summary>
        /// Starts a new bar - call before processing ticks for a new bar.
        /// </summary>
        public void StartNewBar()
        {
            _barDelta = 0;
            _barMaxDelta = 0;
            _barMinDelta = 0;
        }

        /// <summary>
        /// Adds a tick to the current bar with pre-computed isBuy flag.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            long tickDelta = isBuy ? volume : -volume;
            _barDelta += tickDelta;
            _barMaxDelta = Math.Max(_barMaxDelta, _barDelta);
            _barMinDelta = Math.Min(_barMinDelta, _barDelta);

            _lastPrice = price;
        }

        /// <summary>
        /// Adds a tick with explicit buy/sell volume split.
        /// Used for NinjaTrader-style 50/50 split when price equals prior price.
        /// </summary>
        public void AddTickSplit(double price, long totalVolume, long buyVolume, long sellVolume, DateTime tickTime)
        {
            if (price <= 0 || totalVolume <= 0) return;

            long tickDelta = buyVolume - sellVolume;
            _barDelta += tickDelta;
            _barMaxDelta = Math.Max(_barMaxDelta, _barDelta);
            _barMinDelta = Math.Min(_barMinDelta, _barDelta);

            _lastPrice = price;
        }

        /// <summary>
        /// Expires old bar records outside the rolling window.
        /// </summary>
        public void ExpireOldData(DateTime currentTime)
        {
            DateTime cutoffTime = currentTime.AddMinutes(-_rollingWindowMinutes);

            while (_barRecords.Count > 0 && _barRecords.Peek().Time < cutoffTime)
            {
                var oldRecord = _barRecords.Dequeue();
                _rollingCumulativeDelta -= oldRecord.BarDelta;
            }
        }

        /// <summary>
        /// Closes the current bar and returns rolling cumulative delta result.
        /// </summary>
        public CumulativeDeltaResult CloseBar(DateTime barTime)
        {
            // Store bar record for future expiration
            _barRecords.Enqueue(new DeltaBarRecord
            {
                Time = barTime,
                BarDelta = _barDelta
            });

            // Update rolling cumulative
            long prevRollingDelta = _rollingCumulativeDelta;
            _rollingCumulativeDelta += _barDelta;

            var result = new CumulativeDeltaResult
            {
                BarDelta = _barDelta,
                MaxDelta = _barMaxDelta,
                MinDelta = _barMinDelta,
                CumulativeDeltaClose = _rollingCumulativeDelta,
                CumulativeDeltaHigh = prevRollingDelta + _barMaxDelta,
                CumulativeDeltaLow = prevRollingDelta + _barMinDelta,
                Time = barTime,
                IsValid = true
            };

            return result;
        }

        /// <summary>
        /// Gets the current rolling cumulative delta.
        /// </summary>
        public long CurrentCumulativeDelta => _rollingCumulativeDelta + _barDelta;

        /// <summary>
        /// Gets the current bar delta.
        /// </summary>
        public long CurrentBarDelta => _barDelta;
    }
}
