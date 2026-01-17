using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// CircularBuffer with O(1) insertion at front. [0] = most recent value.
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _count;

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _head = 0;
            _count = 0;
        }

        public void Add(T item)
        {
            _head = (_head - 1 + _buffer.Length) % _buffer.Length;
            _buffer[_head] = item;
            if (_count < _buffer.Length) _count++;
        }

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count) throw new IndexOutOfRangeException();
                return _buffer[(_head + index) % _buffer.Length];
            }
        }

        public int Count => _count;

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        public CircularBuffer<T> Clone()
        {
            var clone = new CircularBuffer<T>(_buffer.Length);
            Array.Copy(this._buffer, clone._buffer, this._buffer.Length);
            clone._head = this._head;
            clone._count = this._count;
            return clone;
        }

        public void Restore(CircularBuffer<T> other)
        {
            if (other == null) return;
            Array.Copy(other._buffer, this._buffer, other._buffer.Length);
            this._head = other._head;
            this._count = other._count;
        }
    }

    /// <summary>
    /// Relative metrics engine for VP HVN Buy/Sell.
    /// Tracks time-indexed historical averages and computes Rel/Cum metrics.
    /// Supports both Session VP and Rolling VP metrics.
    /// </summary>
    public class VPRelativeMetricsEngine
    {
        // Configuration
        private const int LOOKBACK_DAYS = 10;       // Days of history for averaging
        private const int MAX_BUFFER_SIZE = 256;    // CircularBuffer capacity
        private const int WARMUP_SECONDS = 15;      // Skip first 15 seconds of session (like NinjaTrader)

        // Time-indexed historical storage (1440 minutes per day)
        // Session: [idx][0]=HVNBuyCount, [1]=HVNSellCount, [2]=ValueWidth
        // Rolling: [idx][0]=RollingHVNBuy, [1]=RollingHVNSell, [2]=RollingValueWidth
        private readonly Dictionary<int, double[]> _avgByTime = new Dictionary<int, double[]>();       // Session averages
        private readonly Dictionary<int, Queue<double>[]> _history = new Dictionary<int, Queue<double>[]>();
        private readonly Dictionary<int, double[]> _rollingAvgByTime = new Dictionary<int, double[]>(); // Rolling averages
        private readonly Dictionary<int, Queue<double>[]> _rollingHistory = new Dictionary<int, Queue<double>[]>();

        // Session cumulative tracking (for Session VP)
        private double[] _sessionCumul = new double[3];  // HVNBuy, HVNSell, ValueWidth
        private double[] _sessionRef = new double[3];
        private DateTime _sessionDate = DateTime.MinValue;
        private DateTime _sessionStartTime = DateTime.MinValue;
        private int _sessionBarCount = 0;

        // Rolling cumulative tracking (for Rolling VP)
        private double[] _rollingCumul = new double[3];  // RollingHVNBuy, RollingHVNSell, RollingValueWidth
        private double[] _rollingRef = new double[3];

        // Session VP Result buffers
        public CircularBuffer<double> RelHVNBuy { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelHVNSell { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelValueWidth { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNBuyRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNSellRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumValueWidthRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);

        public CircularBuffer<double> RelHVNBuyRolling { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelHVNSellRolling { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> RelValueWidthRolling { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNBuyRollingRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumHVNSellRollingRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);
        public CircularBuffer<double> CumValueWidthRollingRank { get; private set; } = new CircularBuffer<double>(MAX_BUFFER_SIZE);

        public VPRelativeMetricsEngine()
        {
            // Initialize time-indexed storage for all 1440 minutes
            for (int i = 0; i < 1440; i++)
            {
                // Session history
                _avgByTime[i] = new double[3];
                _history[i] = new Queue<double>[3];
                for (int j = 0; j < 3; j++)
                    _history[i][j] = new Queue<double>();

                // Rolling history
                _rollingAvgByTime[i] = new double[3];
                _rollingHistory[i] = new Queue<double>[3];
                for (int j = 0; j < 3; j++)
                    _rollingHistory[i][j] = new Queue<double>();
            }
        }

        public VPRelativeMetricsEngine Clone()
        {
            var clone = new VPRelativeMetricsEngine();
            
            // Copy history/averages
            foreach (var kvp in this._avgByTime)
            {
                clone._avgByTime[kvp.Key] = (double[])kvp.Value.Clone();
            }
            foreach (var kvp in this._rollingAvgByTime)
            {
                clone._rollingAvgByTime[kvp.Key] = (double[])kvp.Value.Clone();
            }
            foreach (var kvp in this._history)
            {
                for (int j = 0; j < 3; j++)
                    clone._history[kvp.Key][j] = new Queue<double>(kvp.Value[j]);
            }
            foreach (var kvp in this._rollingHistory)
            {
                for (int j = 0; j < 3; j++)
                    clone._rollingHistory[kvp.Key][j] = new Queue<double>(kvp.Value[j]);
            }

            // Copy session state
            Array.Copy(this._sessionCumul, clone._sessionCumul, 3);
            Array.Copy(this._sessionRef, clone._sessionRef, 3);
            clone._sessionDate = this._sessionDate;
            clone._sessionStartTime = this._sessionStartTime;
            clone._sessionBarCount = this._sessionBarCount;

            Array.Copy(this._rollingCumul, clone._rollingCumul, 3);
            Array.Copy(this._rollingRef, clone._rollingRef, 3);

            // Copy buffers
            clone.RelHVNBuy = this.RelHVNBuy.Clone();
            clone.RelHVNSell = this.RelHVNSell.Clone();
            clone.RelValueWidth = this.RelValueWidth.Clone();
            clone.CumHVNBuyRank = this.CumHVNBuyRank.Clone();
            clone.CumHVNSellRank = this.CumHVNSellRank.Clone();
            clone.CumValueWidthRank = this.CumValueWidthRank.Clone();

            clone.RelHVNBuyRolling = this.RelHVNBuyRolling.Clone();
            clone.RelHVNSellRolling = this.RelHVNSellRolling.Clone();
            clone.RelValueWidthRolling = this.RelValueWidthRolling.Clone();
            clone.CumHVNBuyRollingRank = this.CumHVNBuyRollingRank.Clone();
            clone.CumHVNSellRollingRank = this.CumHVNSellRollingRank.Clone();
            clone.CumValueWidthRollingRank = this.CumValueWidthRollingRank.Clone();

            return clone;
        }

        public void Restore(VPRelativeMetricsEngine other)
        {
            if (other == null) return;

            // Restore history/averages
            foreach (var kvp in other._avgByTime)
            {
                this._avgByTime[kvp.Key] = (double[])kvp.Value.Clone();
            }
            foreach (var kvp in other._rollingAvgByTime)
            {
                this._rollingAvgByTime[kvp.Key] = (double[])kvp.Value.Clone();
            }
            foreach (var kvp in other._history)
            {
                for (int j = 0; j < 3; j++)
                    this._history[kvp.Key][j] = new Queue<double>(kvp.Value[j]);
            }
            foreach (var kvp in other._rollingHistory)
            {
                for (int j = 0; j < 3; j++)
                    this._rollingHistory[kvp.Key][j] = new Queue<double>(kvp.Value[j]);
            }

            // Restore session state
            Array.Copy(other._sessionCumul, this._sessionCumul, 3);
            Array.Copy(other._sessionRef, this._sessionRef, 3);
            this._sessionDate = other._sessionDate;
            this._sessionStartTime = other._sessionStartTime;
            this._sessionBarCount = other._sessionBarCount;

            Array.Copy(other._rollingCumul, this._rollingCumul, 3);
            Array.Copy(other._rollingRef, this._rollingRef, 3);

            // Restore buffers
            this.RelHVNBuy.Restore(other.RelHVNBuy);
            this.RelHVNSell.Restore(other.RelHVNSell);
            this.RelValueWidth.Restore(other.RelValueWidth);
            this.CumHVNBuyRank.Restore(other.CumHVNBuyRank);
            this.CumHVNSellRank.Restore(other.CumHVNSellRank);
            this.CumValueWidthRank.Restore(other.CumValueWidthRank);

            this.RelHVNBuyRolling.Restore(other.RelHVNBuyRolling);
            this.RelHVNSellRolling.Restore(other.RelHVNSellRolling);
            this.RelValueWidthRolling.Restore(other.RelValueWidthRolling);
            this.CumHVNBuyRollingRank.Restore(other.CumHVNBuyRollingRank);
            this.CumHVNSellRollingRank.Restore(other.CumHVNSellRollingRank);
            this.CumValueWidthRollingRank.Restore(other.CumValueWidthRollingRank);
        }

        // ... (Include all the methods like UpdateHistory, UpdateRollingHistory, StartSession, Update, UpdateRolling, etc.)
        // Since I need to include all methods, I'll copy them again here to be safe and complete.

        public void UpdateHistory(DateTime time, double hvnBuyCount, double hvnSellCount, double valueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;
            double[] vals = new double[] { hvnBuyCount, hvnSellCount, valueWidth };

            for (int i = 0; i < 3; i++)
            {
                Queue<double> q = _history[idx][i];
                if (q.Count >= LOOKBACK_DAYS) q.Dequeue();
                q.Enqueue(vals[i]);
                _avgByTime[idx][i] = q.Count > 0 ? q.Average() : vals[i];
            }
        }

        public void UpdateRollingHistory(DateTime time, double rollingHVNBuy, double rollingHVNSell, double rollingValueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;
            double[] vals = new double[] { rollingHVNBuy, rollingHVNSell, rollingValueWidth };

            for (int i = 0; i < 3; i++)
            {
                Queue<double> q = _rollingHistory[idx][i];
                if (q.Count >= LOOKBACK_DAYS) q.Dequeue();
                q.Enqueue(vals[i]);
                _rollingAvgByTime[idx][i] = q.Count > 0 ? q.Average() : vals[i];
            }
        }

        public void StartSession(DateTime sessionStart)
        {
            _sessionDate = sessionStart.Date;
            _sessionStartTime = sessionStart;
            _sessionBarCount = 0;
            for (int i = 0; i < 3; i++)
            {
                _sessionCumul[i] = 0;
                _sessionRef[i] = 0;
                _rollingCumul[i] = 0;
                _rollingRef[i] = 0;
            }

            // Clear Session VP result buffers
            RelHVNBuy.Clear();
            RelHVNSell.Clear();
            RelValueWidth.Clear();
            CumHVNBuyRank.Clear();
            CumHVNSellRank.Clear();
            CumValueWidthRank.Clear();

            // Clear Rolling VP result buffers
            RelHVNBuyRolling.Clear();
            RelHVNSellRolling.Clear();
            RelValueWidthRolling.Clear();
            CumHVNBuyRollingRank.Clear();
            CumHVNSellRollingRank.Clear();
            CumValueWidthRollingRank.Clear();
        }

        public void Update(DateTime time, double hvnBuyCount, double hvnSellCount, double valueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;

            if (time.Date != _sessionDate)
            {
                StartSession(time);
            }

            double[] reference = GetReferenceMetrics(idx);
            double[] current = new double[] { hvnBuyCount, hvnSellCount, valueWidth };

            bool isWarmup = _sessionStartTime != DateTime.MinValue &&
                           (time - _sessionStartTime).TotalSeconds < WARMUP_SECONDS;

            double[] relativeValues = new double[3];
            double[] cumulativeValues = new double[3];

            for (int i = 0; i < 3; i++)
            {
                if (reference[i] > 0 && current[i] > 0)
                    relativeValues[i] = (current[i] / reference[i]) * 100;
                else
                    relativeValues[i] = 0;

                if (!isWarmup)
                {
                    if (reference[i] > 0 && current[i] > 0)
                    {
                        _sessionCumul[i] += current[i];
                        _sessionRef[i] += reference[i];

                        if (_sessionRef[i] > 0)
                            cumulativeValues[i] = (_sessionCumul[i] / _sessionRef[i]) * 100;
                        else
                            cumulativeValues[i] = 100;
                    }
                    else if (_sessionBarCount > 0)
                    {
                        cumulativeValues[i] = GetPreviousCumulativeValue(i);
                    }
                    else
                    {
                        cumulativeValues[i] = 100;
                    }
                }
                else
                {
                    cumulativeValues[i] = 100;
                }
            }

            RelHVNBuy.Add(Math.Round(relativeValues[0], 2));
            RelHVNSell.Add(Math.Round(relativeValues[1], 2));
            RelValueWidth.Add(Math.Round(relativeValues[2], 2));
            CumHVNBuyRank.Add(Math.Round(cumulativeValues[0], 2));
            CumHVNSellRank.Add(Math.Round(cumulativeValues[1], 2));
            CumValueWidthRank.Add(Math.Round(cumulativeValues[2], 2));

            _sessionBarCount++;
        }

        public (double cumHVNBuy, double refHVNBuy, double cumHVNSell, double refHVNSell, double cumValWidth, double refValWidth) GetSessionTotals()
        {
            return (_sessionCumul[0], _sessionRef[0], _sessionCumul[1], _sessionRef[1], _sessionCumul[2], _sessionRef[2]);
        }

        private double[] GetReferenceMetrics(int idx)
        {
            double[] result = new double[3];
            int[] windowSizes = new int[] { 10, 30, 60 };

            for (int i = 0; i < 3; i++)
            {
                result[i] = GetWeightedReference(idx, i, windowSizes);
            }

            return result;
        }

        private double GetWeightedReference(int timeIdx, int dataIndex, int[] windowSizes)
        {
            if (_avgByTime.ContainsKey(timeIdx) && _avgByTime[timeIdx][dataIndex] > 0)
                return _avgByTime[timeIdx][dataIndex];

            foreach (int windowSize in windowSizes)
            {
                double totalWeight = 0;
                double weightedSum = 0;
                for (int offset = -windowSize; offset <= windowSize; offset++)
                {
                    int tIdx = (timeIdx + offset + 1440) % 1440;
                    if (_avgByTime.ContainsKey(tIdx) && _avgByTime[tIdx][dataIndex] > 0)
                    {
                        double weight = 1.0 / (Math.Abs(offset) + 1);
                        weightedSum += _avgByTime[tIdx][dataIndex] * weight;
                        totalWeight += weight;
                    }
                }
                if (totalWeight > 0) return weightedSum / totalWeight;
            }

            double sum = 0;
            int count = 0;
            foreach (var kvp in _avgByTime)
            {
                if (kvp.Value[dataIndex] > 0) { sum += kvp.Value[dataIndex]; count++; }
            }
            return count > 0 ? sum / count : 0;
        }

        private double GetPreviousCumulativeValue(int index)
        {
            return index switch
            {
                0 => CumHVNBuyRank.Count > 0 ? CumHVNBuyRank[0] : 100,
                1 => CumHVNSellRank.Count > 0 ? CumHVNSellRank[0] : 100,
                2 => CumValueWidthRank.Count > 0 ? CumValueWidthRank[0] : 100,
                _ => 100
            };
        }

        public void UpdateRolling(DateTime time, double rollingHVNBuy, double rollingHVNSell, double rollingValueWidth)
        {
            int idx = time.Hour * 60 + time.Minute;

            double[] reference = GetRollingReferenceMetrics(idx);
            double[] current = new double[] { rollingHVNBuy, rollingHVNSell, rollingValueWidth };

            bool isWarmup = _sessionStartTime != DateTime.MinValue &&
                           (time - _sessionStartTime).TotalSeconds < WARMUP_SECONDS;

            double[] relativeValues = new double[3];
            double[] cumulativeValues = new double[3];

            for (int i = 0; i < 3; i++)
            {
                if (reference[i] > 0 && current[i] > 0)
                    relativeValues[i] = (current[i] / reference[i]) * 100;
                else
                    relativeValues[i] = 0;

                if (!isWarmup)
                {
                    if (reference[i] > 0 && current[i] > 0)
                    {
                        _rollingCumul[i] += current[i];
                        _rollingRef[i] += reference[i];

                        if (_rollingRef[i] > 0)
                            cumulativeValues[i] = (_rollingCumul[i] / _rollingRef[i]) * 100;
                        else
                            cumulativeValues[i] = 100;
                    }
                    else if (_sessionBarCount > 0)
                    {
                        cumulativeValues[i] = GetPreviousRollingCumulativeValue(i);
                    }
                    else
                    {
                        cumulativeValues[i] = 100;
                    }
                }
                else
                {
                    cumulativeValues[i] = 100;
                }
            }

            RelHVNBuyRolling.Add(Math.Round(relativeValues[0], 2));
            RelHVNSellRolling.Add(Math.Round(relativeValues[1], 2));
            RelValueWidthRolling.Add(Math.Round(relativeValues[2], 2));
            CumHVNBuyRollingRank.Add(Math.Round(cumulativeValues[0], 2));
            CumHVNSellRollingRank.Add(Math.Round(cumulativeValues[1], 2));
            CumValueWidthRollingRank.Add(Math.Round(cumulativeValues[2], 2));
        }

        private double[] GetRollingReferenceMetrics(int idx)
        {
            double[] result = new double[3];
            int[] windowSizes = new int[] { 10, 30, 60 };

            for (int i = 0; i < 3; i++)
            {
                result[i] = GetWeightedRollingReference(idx, i, windowSizes);
            }

            return result;
        }

        private double GetWeightedRollingReference(int timeIdx, int dataIndex, int[] windowSizes)
        {
            if (_rollingAvgByTime.ContainsKey(timeIdx) && _rollingAvgByTime[timeIdx][dataIndex] > 0)
                return _rollingAvgByTime[timeIdx][dataIndex];

            foreach (int windowSize in windowSizes)
            {
                double totalWeight = 0;
                double weightedSum = 0;
                for (int offset = -windowSize; offset <= windowSize; offset++)
                {
                    int tIdx = (timeIdx + offset + 1440) % 1440;
                    if (_rollingAvgByTime.ContainsKey(tIdx) && _rollingAvgByTime[tIdx][dataIndex] > 0)
                    {
                        double weight = 1.0 / (Math.Abs(offset) + 1);
                        weightedSum += _rollingAvgByTime[tIdx][dataIndex] * weight;
                        totalWeight += weight;
                    }
                }
                if (totalWeight > 0) return weightedSum / totalWeight;
            }

            double sum = 0;
            int count = 0;
            foreach (var kvp in _rollingAvgByTime)
            {
                if (kvp.Value[dataIndex] > 0) { sum += kvp.Value[dataIndex]; count++; }
            }
            return count > 0 ? sum / count : 0;
        }

        private double GetPreviousRollingCumulativeValue(int index)
        {
            return index switch
            {
                0 => CumHVNBuyRollingRank.Count > 0 ? CumHVNBuyRollingRank[0] : 100,
                1 => CumHVNSellRollingRank.Count > 0 ? CumHVNSellRollingRank[0] : 100,
                2 => CumValueWidthRollingRank.Count > 0 ? CumValueWidthRollingRank[0] : 100,
                _ => 100
            };
        }
    }
}
