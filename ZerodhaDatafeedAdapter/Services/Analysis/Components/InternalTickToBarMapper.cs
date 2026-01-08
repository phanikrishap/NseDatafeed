using System;
using System.Collections.Generic;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Efficient tick-to-bar mapper using index ranges.
    /// Provides O(1) lookup for ticks within a RangeATR bar.
    /// </summary>
    public class InternalTickToBarMapper
    {
        private readonly List<RangeATRBar> _bars;
        private readonly List<HistoricalTick> _ticks;

        // Maps bar index to (startTickIndex, endTickIndex)
        private readonly Dictionary<int, (int start, int end)> _barToTickRange;

        public int MappedBarsCount => _barToTickRange.Count;

        public InternalTickToBarMapper(List<RangeATRBar> bars, List<HistoricalTick> ticks)
        {
            _bars = bars ?? new List<RangeATRBar>();
            _ticks = ticks ?? new List<HistoricalTick>();
            _barToTickRange = new Dictionary<int, (int start, int end)>();
        }

        /// <summary>
        /// Builds the tick-to-bar index mapping.
        /// </summary>
        public void BuildIndex()
        {
            if (_bars.Count == 0 || _ticks.Count == 0)
                return;

            for (int i = 0; i < _bars.Count; i++)
            {
                var currentBar = _bars[i];

                DateTime barStartTime;
                DateTime barEndTime = currentBar.Time;

                if (i > 0)
                {
                    barStartTime = _bars[i - 1].Time;
                }
                else
                {
                    barStartTime = currentBar.Time.AddMinutes(-5);
                }

                // If bar time is very far from previous, clamp it? 
                // Original logic:
                // barStartTime = _bars[i - 1].Time;

                int startIdx = BinarySearchTickIndex(barStartTime, true);
                int endIdx = BinarySearchTickIndex(barEndTime, false);

                if (startIdx >= 0 && endIdx >= 0 && startIdx <= endIdx)
                {
                    _barToTickRange[currentBar.Index] = (startIdx, endIdx);
                }
            }
        }

        private int BinarySearchTickIndex(DateTime targetTime, bool findFirst)
        {
            if (_ticks.Count == 0) return -1;

            int left = 0;
            int right = _ticks.Count - 1;
            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var midTime = _ticks[mid].Time;

                if (midTime == targetTime)
                {
                    result = mid;
                    if (findFirst)
                        right = mid - 1;
                    else
                        left = mid + 1;
                }
                else if (midTime < targetTime)
                {
                    if (!findFirst) result = mid;
                    left = mid + 1;
                }
                else
                {
                    if (findFirst) result = mid;
                    right = mid - 1;
                }
            }

            if (result == -1)
            {
                result = findFirst ? left : right;
                result = Math.Max(0, Math.Min(result, _ticks.Count - 1));
            }

            return result;
        }

        /// <summary>
        /// Gets all ticks that belong to a specific bar.
        /// </summary>
        public List<HistoricalTick> GetTicksForBar(int barIndex)
        {
            if (!_barToTickRange.TryGetValue(barIndex, out var range))
                return new List<HistoricalTick>();

            var result = new List<HistoricalTick>(range.end - range.start + 1);

            for (int i = range.start; i <= range.end && i < _ticks.Count; i++)
            {
                result.Add(_ticks[i]);
            }

            return result;
        }
    }
}
