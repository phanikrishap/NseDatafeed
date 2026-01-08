using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Stores a volume update for rolling window expiration.
    /// </summary>
    public class RollingVolumeUpdate
    {
        public double Price { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>
    /// Rolling Volume Profile Engine - maintains a time-windowed VP (60 minutes by default).
    /// Supports incremental updates with automatic expiration of old data.
    /// </summary>
    public class RollingVolumeProfileEngine
    {
        private readonly Queue<RollingVolumeUpdate> _updates = new Queue<RollingVolumeUpdate>();
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;
        private int _rollingWindowMinutes = 60;
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;
        private double _lastClosePrice = 0;

        public RollingVolumeProfileEngine(int rollingWindowMinutes = 60)
        {
            _rollingWindowMinutes = rollingWindowMinutes;
        }

        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _volumeAtPrice.Clear();
            _updates.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
        }

        /// <summary>
        /// Adds a tick to the rolling VP with timestamp for expiration tracking.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            double roundedPrice = Math.Round(price / _priceInterval) * _priceInterval;

            // Store update for rolling window management
            _updates.Enqueue(new RollingVolumeUpdate
            {
                Price = roundedPrice,
                Volume = volume,
                IsBuy = isBuy,
                Time = tickTime
            });

            // Add to volume profile
            if (!_volumeAtPrice.ContainsKey(roundedPrice))
            {
                _volumeAtPrice[roundedPrice] = new VolumePriceLevel { Price = roundedPrice };
            }

            var level = _volumeAtPrice[roundedPrice];
            level.Volume += volume;
            if (isBuy)
                level.BuyVolume += volume;
            else
                level.SellVolume += volume;

            _totalVolume += volume;
            _sumPriceVolume += price * volume;
        }

        /// <summary>
        /// Removes expired data outside the rolling window.
        /// </summary>
        public void ExpireOldData(DateTime currentTime)
        {
            DateTime cutoffTime = currentTime.AddMinutes(-_rollingWindowMinutes);

            while (_updates.Count > 0 && _updates.Peek().Time < cutoffTime)
            {
                var oldUpdate = _updates.Dequeue();
                RemoveVolume(oldUpdate);
            }
        }

        private void RemoveVolume(RollingVolumeUpdate update)
        {
            if (!_volumeAtPrice.ContainsKey(update.Price)) return;

            var level = _volumeAtPrice[update.Price];
            level.Volume -= update.Volume;
            if (update.IsBuy)
                level.BuyVolume -= update.Volume;
            else
                level.SellVolume -= update.Volume;

            _totalVolume -= update.Volume;
            _sumPriceVolume -= update.Price * update.Volume;

            // Remove price level if empty
            if (level.Volume <= 0)
            {
                _volumeAtPrice.Remove(update.Price);
            }
        }

        public void SetClosePrice(double closePrice)
        {
            _lastClosePrice = closePrice;
        }

        public InternalVPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new InternalVPResult();

            if (_volumeAtPrice.Count == 0 || _totalVolume <= 0)
            {
                result.IsValid = false;
                return result;
            }

            // Find POC
            double maxVolume = 0;
            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume > maxVolume)
                {
                    maxVolume = level.Value.Volume;
                    result.POC = level.Key;
                }
            }

            // Calculate VWAP
            result.VWAP = _totalVolume > 0 ? _sumPriceVolume / _totalVolume : 0;

            // Bidirectional expansion for Value Area
            var sortedLevels = _volumeAtPrice.Where(l => l.Value.Volume > 0).OrderBy(l => l.Key).ToList();
            if (sortedLevels.Count == 0)
            {
                result.IsValid = false;
                return result;
            }

            int pocIndex = sortedLevels.FindIndex(l => l.Key == result.POC);
            if (pocIndex < 0) pocIndex = 0;

            double targetVolume = _totalVolume * valueAreaPercent;
            double accumulatedVolume = maxVolume;

            int upperIndex = pocIndex;
            int lowerIndex = pocIndex;

            while (accumulatedVolume < targetVolume && (upperIndex < sortedLevels.Count - 1 || lowerIndex > 0))
            {
                double upperVolume = (upperIndex < sortedLevels.Count - 1) ?
                    sortedLevels[upperIndex + 1].Value.Volume : 0;
                double lowerVolume = (lowerIndex > 0) ?
                    sortedLevels[lowerIndex - 1].Value.Volume : 0;

                if (upperVolume >= lowerVolume && upperIndex < sortedLevels.Count - 1)
                {
                    upperIndex++;
                    accumulatedVolume += upperVolume;
                }
                else if (lowerIndex > 0)
                {
                    lowerIndex--;
                    accumulatedVolume += lowerVolume;
                }
                else
                {
                    break;
                }
            }

            result.VAH = sortedLevels[upperIndex].Key;
            result.VAL = sortedLevels[lowerIndex].Key;

            // Calculate HVNs
            double hvnThreshold = maxVolume * hvnRatio;
            result.HVNs = new List<double>();
            result.HVNBuyCount = 0;
            result.HVNSellCount = 0;
            result.HVNBuyVolume = 0;
            result.HVNSellVolume = 0;

            double classificationPrice = _lastClosePrice > 0 ? _lastClosePrice : result.POC;

            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume >= hvnThreshold)
                {
                    result.HVNs.Add(level.Key);

                    if (level.Key <= classificationPrice)
                    {
                        result.HVNBuyCount++;
                        result.HVNBuyVolume += level.Value.Volume;
                    }
                    else
                    {
                        result.HVNSellCount++;
                        result.HVNSellVolume += level.Value.Volume;
                    }
                }
            }

            result.HVNs = result.HVNs.OrderBy(h => h).ToList();
            result.IsValid = true;

            return result;
        }
    }
}
