using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Momentum bias direction (from smaMomentum).
    /// </summary>
    public enum MomentumBias
    {
        Neutral = 0,
        Bullish = 1,
        Bearish = -1
    }

    /// <summary>
    /// Direction of series movement.
    /// </summary>
    public enum SeriesDirection
    {
        Neutral = 0,
        Rising = 1,
        Falling = -1
    }

    /// <summary>
    /// Result of Momentum calculation for a bar.
    /// Replicates smaMomentum logic:
    /// - Momentum = Input - DoubleSMA(Input, period+1)
    /// - Smooth = EHMA(Momentum, smoothPeriod)
    /// - Bias based on alignment of Momentum, Smooth, and direction
    /// </summary>
    public class MomentumResult
    {
        /// <summary>
        /// Raw momentum value: Input - DoubleSMA(Input, period+1).
        /// </summary>
        public double Momentum { get; set; }

        /// <summary>
        /// Smoothed momentum (EHMA of Momentum).
        /// </summary>
        public double Smooth { get; set; }

        /// <summary>
        /// Previous smooth value (for direction detection).
        /// </summary>
        public double PrevSmooth { get; set; }

        /// <summary>
        /// Direction of smoothed momentum.
        /// </summary>
        public SeriesDirection SmoothDirection { get; set; }

        /// <summary>
        /// Momentum bias: +1 bullish, -1 bearish, 0 neutral.
        /// Bullish: Momentum > 0, Smooth > 0, Momentum > Smooth, Smooth rising
        /// Bearish: Momentum < 0, Smooth < 0, Momentum < Smooth, Smooth falling
        /// </summary>
        public MomentumBias Bias { get; set; }

        /// <summary>
        /// True if this bar represents a peak in smooth momentum.
        /// </summary>
        public bool IsPeak { get; set; }

        /// <summary>
        /// Last detected peak value.
        /// </summary>
        public double LastPeak { get; set; }

        /// <summary>
        /// Last detected trough value.
        /// </summary>
        public double LastTrough { get; set; }

        /// <summary>
        /// Bar timestamp.
        /// </summary>
        public DateTime Time { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Simple Moving Average calculator with ring buffer.
    /// </summary>
    public class SMACalculator
    {
        private readonly double[] _buffer;
        private readonly int _period;
        private int _index = 0;
        private int _count = 0;
        private double _sum = 0;

        public SMACalculator(int period)
        {
            _period = Math.Max(1, period);
            _buffer = new double[_period];
        }

        public void Reset()
        {
            _index = 0;
            _count = 0;
            _sum = 0;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public double Add(double value)
        {
            // Remove old value from sum if buffer is full
            if (_count >= _period)
            {
                _sum -= _buffer[_index];
            }
            else
            {
                _count++;
            }

            // Add new value
            _buffer[_index] = value;
            _sum += value;

            // Move index
            _index = (_index + 1) % _period;

            return _count > 0 ? _sum / _count : 0;
        }

        public double CurrentSMA => _count > 0 ? _sum / _count : 0;
        public bool IsFull => _count >= _period;

        public SMACalculator Clone()
        {
            var clone = new SMACalculator(_period);
            Array.Copy(this._buffer, clone._buffer, this._buffer.Length);
            clone._index = this._index;
            clone._count = this._count;
            clone._sum = this._sum;
            return clone;
        }

        public void Restore(SMACalculator other)
        {
            if (other == null) return;
            Array.Copy(other._buffer, this._buffer, this._buffer.Length);
            this._index = other._index;
            this._count = other._count;
            this._sum = other._sum;
        }
    }

    /// <summary>
    /// Exponential Hull Moving Average (EHMA) calculator.
    /// Matches smaEHMA.cs implementation:
    /// k1 = 2.0 / (1 + Period)
    /// k2 = 2.0 / (1 + 0.5 * Period)
    /// k3 = 2.0 / (1 + Math.Sqrt(Period))
    /// ema1[0] = k1 * Input[0] + (1 - k1) * ema1[1]
    /// ema2[0] = k2 * Input[0] + (1 - k2) * ema2[1]
    /// EHMA[0] = k3 * (2*ema2[0] - ema1[0]) + (1 - k3) * EHMA[1]
    /// </summary>
    public class EHMACalculator
    {
        private readonly double _k1;
        private readonly double _k2;
        private readonly double _k3;
        private double _ema1 = 0;
        private double _ema2 = 0;
        private double _ehma = 0;
        private bool _initialized = false;

        public EHMACalculator(int period)
        {
            int p = Math.Max(1, period);
            _k1 = 2.0 / (1 + p);
            _k2 = 2.0 / (1 + 0.5 * p);
            _k3 = 2.0 / (1 + Math.Sqrt(p));
        }

        public void Reset()
        {
            _ema1 = 0;
            _ema2 = 0;
            _ehma = 0;
            _initialized = false;
        }

        public double Add(double value)
        {
            if (!_initialized)
            {
                _ema1 = value;
                _ema2 = value;
                _ehma = value;
                _initialized = true;
            }
            else
            {
                _ema1 = _k1 * value + (1 - _k1) * _ema1;
                _ema2 = _k2 * value + (1 - _k2) * _ema2;
                _ehma = _k3 * (2 * _ema2 - _ema1) + (1 - _k3) * _ehma;
            }
            return _ehma;
        }

        public double CurrentEHMA => _ehma;

        public EHMACalculator Clone()
        {
            var clone = new EHMACalculator(1); // dummy period, we will restore state
            // Manual field copy as constructor recalculates constants
            typeof(EHMACalculator).GetField("_k1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(clone, this._k1);
            typeof(EHMACalculator).GetField("_k2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(clone, this._k2);
            typeof(EHMACalculator).GetField("_k3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(clone, this._k3);
            clone._ema1 = this._ema1;
            clone._ema2 = this._ema2;
            clone._ehma = this._ehma;
            clone._initialized = this._initialized;
            return clone;
        }

        public void Restore(EHMACalculator other)
        {
            if (other == null) return;
            // Constants are fixed in constructor, but we copy them anyway for correctness
            typeof(EHMACalculator).GetField("_k1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, other._k1);
            typeof(EHMACalculator).GetField("_k2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, other._k2);
            typeof(EHMACalculator).GetField("_k3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, other._k3);
            this._ema1 = other._ema1;
            this._ema2 = other._ema2;
            this._ehma = other._ehma;
            this._initialized = other._initialized;
        }
    }

    /// <summary>
    /// Momentum Engine - Computes momentum from price/value series.
    /// Replicates smaMomentum logic exactly:
    ///
    /// Momentum = Input - SMA(SMA(Input, lookback+1), lookback+1)  [Double-smoothed SMA]
    /// Smooth = EHMA(Momentum, smoothPeriod)  [Exponential Hull MA, NOT regular EMA]
    ///
    /// Bias:
    /// - Bullish (+1): Momentum > 0, Smooth > 0, Momentum > Smooth, Smooth rising
    /// - Bearish (-1): Momentum < 0, Smooth < 0, Momentum < Smooth, Smooth falling
    /// - Neutral (0): Otherwise
    ///
    /// Also tracks peaks and troughs of the smoothed momentum.
    /// </summary>
    public class MomentumEngine
    {
        private readonly int _momentumPeriod;
        private readonly int _smoothPeriod;

        // Double SMA calculation (uses period + 1 to match smaMomentum)
        private readonly SMACalculator _sma1;
        private readonly SMACalculator _sma2;

        // Smooth calculation (EHMA of momentum - matches smaMomentum exactly)
        private readonly EHMACalculator _smoothEhma;

        // Peak/Trough tracking
        private double _lastPeak = 0;
        private double _lastTrough = 0;
        private double _lastUpPeak = 0;
        private double _lastDownPeak = 0;

        // Previous values for direction detection
        private double _prevSmooth = 0;
        private double _prevPrevSmooth = 0;
        private SeriesDirection _lastDirection = SeriesDirection.Neutral;

        // Bar tracking
        private int _barCount = 0;

        public MomentumEngine(int momentumPeriod = 14, int smoothPeriod = 7)
        {
            _momentumPeriod = Math.Max(1, momentumPeriod);
            _smoothPeriod = Math.Max(1, smoothPeriod);

            // Use period + 1 for SMA lookback (matches smaMomentum: SMA(SMA(Input, lookback + 1), lookback + 1))
            _sma1 = new SMACalculator(_momentumPeriod + 1);
            _sma2 = new SMACalculator(_momentumPeriod + 1);
            _smoothEhma = new EHMACalculator(_smoothPeriod);
        }

        /// <summary>
        /// Resets the engine.
        /// </summary>
        public void Reset()
        {
            _sma1.Reset();
            _sma2.Reset();
            _smoothEhma.Reset();
            _lastPeak = 0;
            _lastTrough = 0;
            _lastUpPeak = 0;
            _lastDownPeak = 0;
            _prevSmooth = 0;
            _prevPrevSmooth = 0;
            _lastDirection = SeriesDirection.Neutral;
            _barCount = 0;
        }

        /// <summary>
        /// Processes a new bar value and computes momentum.
        /// </summary>
        /// <param name="value">Input value (price, cumulative delta, etc.)</param>
        /// <param name="barTime">Bar timestamp</param>
        /// <returns>Momentum result</returns>
        public MomentumResult ProcessBar(double value, DateTime barTime)
        {
            _barCount++;

            var result = new MomentumResult
            {
                Time = barTime,
                IsValid = true
            };

            // Calculate double-smoothed SMA: SMA(SMA(Input, period+1), period+1)
            double sma1Value = _sma1.Add(value);
            double doubleSma = _sma2.Add(sma1Value);

            // Momentum = Input - DoubleSMA (0 for first bar, matches smaMomentum)
            double momentum = _barCount <= 1 ? 0 : value - doubleSma;
            result.Momentum = momentum;

            // Smooth = EHMA(Momentum) - uses EHMA, NOT regular EMA (matches smaMomentum exactly)
            double smooth = _smoothEhma.Add(momentum);
            result.Smooth = smooth;
            result.PrevSmooth = _prevSmooth;

            // Determine direction
            SeriesDirection direction = SeriesDirection.Neutral;
            if (smooth > _prevSmooth)
                direction = SeriesDirection.Rising;
            else if (smooth < _prevSmooth)
                direction = SeriesDirection.Falling;

            result.SmoothDirection = direction;

            // Track peaks and troughs (matches smaMomentum Series class logic)
            if (direction != _lastDirection && _lastDirection != SeriesDirection.Neutral)
            {
                if (_lastDirection == SeriesDirection.Rising)
                    _lastPeak = _prevSmooth;
                else if (_lastDirection == SeriesDirection.Falling)
                    _lastTrough = _prevSmooth;
            }

            result.LastPeak = _lastPeak;
            result.LastTrough = _lastTrough;

            // Reset up/down peaks on zero crossing (matches smaMomentum)
            if (_prevSmooth <= 0 && smooth > 0)
                _lastUpPeak = 0;
            if (_prevSmooth >= 0 && smooth < 0)
                _lastDownPeak = 0;

            // Update last up/down peaks
            if (smooth > 0 && _lastPeak > 0)
                _lastUpPeak = _lastPeak;
            if (smooth < 0 && _lastTrough < 0)
                _lastDownPeak = _lastTrough;

            // Determine bias (matches smaMomentum logic exactly)
            // Bullish: Momentum > 0, Smooth > 0, Momentum > Smooth, Smooth rising
            // Bearish: Momentum < 0, Smooth < 0, Momentum < Smooth, Smooth falling
            MomentumBias bias = MomentumBias.Neutral;
            bool isPeak = false;

            if (momentum > 0 && smooth > 0 && momentum > smooth && direction == SeriesDirection.Rising)
            {
                bias = MomentumBias.Bullish;
                // Peak condition (matches smaMomentum)
                if ((_lastUpPeak == 0 && smooth >= -_lastDownPeak) ||
                    (_lastUpPeak > 0 && smooth >= _lastUpPeak))
                {
                    isPeak = true;
                }
            }
            else if (momentum < 0 && smooth < 0 && momentum < smooth && direction == SeriesDirection.Falling)
            {
                bias = MomentumBias.Bearish;
                // Trough/peak condition (matches smaMomentum)
                if ((_lastDownPeak == 0 && smooth <= -_lastUpPeak) ||
                    (_lastDownPeak < 0 && smooth <= _lastDownPeak))
                {
                    isPeak = true;
                }
            }

            result.Bias = bias;
            result.IsPeak = isPeak;

            // Store for next iteration
            _prevPrevSmooth = _prevSmooth;
            _prevSmooth = smooth;
            _lastDirection = direction;

            return result;
        }

        /// <summary>
        /// Gets the current smooth value.
        /// </summary>
        public double CurrentSmooth => _prevSmooth;

        public MomentumEngine Clone()
        {
            var clone = new MomentumEngine(_momentumPeriod, _smoothPeriod);
            clone._sma1.Restore(this._sma1.Clone());
            clone._sma2.Restore(this._sma2.Clone());
            clone._smoothEhma.Restore(this._smoothEhma.Clone());
            clone._lastPeak = this._lastPeak;
            clone._lastTrough = this._lastTrough;
            clone._lastUpPeak = this._lastUpPeak;
            clone._lastDownPeak = this._lastDownPeak;
            clone._prevSmooth = this._prevSmooth;
            clone._prevPrevSmooth = this._prevPrevSmooth;
            clone._lastDirection = this._lastDirection;
            clone._barCount = this._barCount;
            return clone;
        }

        public void Restore(MomentumEngine other)
        {
            if (other == null) return;
            this._sma1.Restore(other._sma1.Clone());
            this._sma2.Restore(other._sma2.Clone());
            this._smoothEhma.Restore(other._smoothEhma.Clone());
            this._lastPeak = other._lastPeak;
            this._lastTrough = other._lastTrough;
            this._lastUpPeak = other._lastUpPeak;
            this._lastDownPeak = other._lastDownPeak;
            this._prevSmooth = other._prevSmooth;
            this._prevPrevSmooth = other._prevPrevSmooth;
            this._lastDirection = other._lastDirection;
            this._barCount = other._barCount;
        }
    }

    /// <summary>
    /// CD Momentum Engine - Momentum calculated on Cumulative Delta.
    /// Combines CumulativeDeltaEngine with MomentumEngine.
    /// Replicates smaCDMomentum: applies smaMomentum to cumulative delta values.
    /// </summary>
    public class CDMomentumEngine
    {
        private readonly CumulativeDeltaEngine _deltaEngine;
        private readonly MomentumEngine _momentumEngine;

        public CDMomentumEngine(int momentumPeriod = 14, int smoothPeriod = 7)
        {
            _deltaEngine = new CumulativeDeltaEngine();
            _momentumEngine = new MomentumEngine(momentumPeriod, smoothPeriod);
        }

        /// <summary>
        /// Resets both engines.
        /// </summary>
        public void Reset()
        {
            _deltaEngine.Reset();
            _momentumEngine.Reset();
        }

        /// <summary>
        /// Starts a new bar.
        /// </summary>
        public void StartNewBar()
        {
            _deltaEngine.StartNewBar();
        }

        /// <summary>
        /// Adds a tick to the current bar.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            _deltaEngine.AddTick(price, volume, isBuy, tickTime);
        }

        /// <summary>
        /// Closes the current bar and computes CD momentum.
        /// </summary>
        public MomentumResult CloseBar(DateTime barTime)
        {
            var deltaResult = _deltaEngine.CloseBar(barTime);

            // Apply momentum to cumulative delta close value (matches smaCDMomentum)
            return _momentumEngine.ProcessBar(deltaResult.CumulativeDeltaClose, barTime);
        }

        /// <summary>
        /// Gets the cumulative delta.
        /// </summary>
        public long CurrentCumulativeDelta => _deltaEngine.CurrentCumulativeDelta;

        public CDMomentumEngine Clone()
        {
            var clone = new CDMomentumEngine();
            clone._deltaEngine.Restore(this._deltaEngine.Clone());
            clone._momentumEngine.Restore(this._momentumEngine.Clone());
            return clone;
        }

        public void Restore(CDMomentumEngine other)
        {
            if (other == null) return;
            this._deltaEngine.Restore(other._deltaEngine.Clone());
            this._momentumEngine.Restore(other._momentumEngine.Clone());
        }
    }

    /// <summary>
    /// Rolling CD Momentum Engine - Momentum on rolling cumulative delta.
    /// </summary>
    public class RollingCDMomentumEngine
    {
        private readonly RollingCumulativeDeltaEngine _deltaEngine;
        private readonly MomentumEngine _momentumEngine;

        public RollingCDMomentumEngine(int rollingWindowMinutes = 60, int momentumPeriod = 14, int smoothPeriod = 7)
        {
            _deltaEngine = new RollingCumulativeDeltaEngine(rollingWindowMinutes);
            _momentumEngine = new MomentumEngine(momentumPeriod, smoothPeriod);
        }

        /// <summary>
        /// Resets both engines.
        /// </summary>
        public void Reset()
        {
            _deltaEngine.Reset();
            _momentumEngine.Reset();
        }

        /// <summary>
        /// Starts a new bar.
        /// </summary>
        public void StartNewBar()
        {
            _deltaEngine.StartNewBar();
        }

        /// <summary>
        /// Adds a tick to the current bar.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            _deltaEngine.AddTick(price, volume, isBuy, tickTime);
        }

        /// <summary>
        /// Expires old data from the rolling window.
        /// </summary>
        public void ExpireOldData(DateTime currentTime)
        {
            _deltaEngine.ExpireOldData(currentTime);
        }

        /// <summary>
        /// Closes the current bar and computes rolling CD momentum.
        /// </summary>
        public MomentumResult CloseBar(DateTime barTime)
        {
            var deltaResult = _deltaEngine.CloseBar(barTime);

            // Apply momentum to rolling cumulative delta
            return _momentumEngine.ProcessBar(deltaResult.CumulativeDeltaClose, barTime);
        }

        /// <summary>
        /// Gets the rolling cumulative delta.
        /// </summary>
        public long CurrentCumulativeDelta => _deltaEngine.CurrentCumulativeDelta;
    }
}
