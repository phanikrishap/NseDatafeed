using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Volume at a price level with buy/sell breakdown.
    /// </summary>
    public class VolumePriceLevel
    {
        public double Price { get; set; }
        public long Volume { get; set; }
        public long BuyVolume { get; set; }
        public long SellVolume { get; set; }
    }

    /// <summary>
    /// Result of VP calculation including HVN Buy/Sell breakdown.
    /// </summary>
    public class InternalVPResult
    {
        public double POC { get; set; }
        public double VAH { get; set; }
        public double VAL { get; set; }
        public double VWAP { get; set; }
        public List<double> HVNs { get; set; } = new List<double>();
        public double ValueWidth => VAH - VAL;  // Value area width

        // HVN Buy/Sell counts - how many HVNs are dominated by buyers vs sellers
        public int HVNBuyCount { get; set; }    // HVNs where BuyVolume > SellVolume
        public int HVNSellCount { get; set; }   // HVNs where SellVolume > BuyVolume

        // Total buy/sell volumes across all HVN levels
        public long HVNBuyVolume { get; set; }
        public long HVNSellVolume { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Internal simplified Volume Profile Engine.
    /// Computes POC, VAH, VAL, VWAP, HVNs from tick data.
    /// Uses configurable price interval for price bucketing (1 rupee for NIFTY, not tick size).
    /// </summary>
    public class InternalVolumeProfileEngine
    {
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;  // Price interval for VP buckets (1 rupee for NIFTY)
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;

        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _volumeAtPrice.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
        }

        public void AddTick(double price, long volume, bool isBuy)
        {
            if (price <= 0 || volume <= 0) return;

            double roundedPrice = Math.Round(price / _priceInterval) * _priceInterval;

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

        // Track last close price for HVN classification
        private double _lastClosePrice = 0;

        public void SetClosePrice(double closePrice)
        {
            _lastClosePrice = closePrice;
        }

        public InternalVPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new InternalVPResult();

            if (_volumeAtPrice.Count == 0 || _totalVolume == 0)
            {
                result.IsValid = false;
                return result;
            }

            // Find POC (price with highest volume)
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
            result.VWAP = _sumPriceVolume / _totalVolume;

            // Bidirectional expansion for Value Area
            var sortedLevels = _volumeAtPrice.OrderBy(l => l.Key).ToList();
            int pocIndex = sortedLevels.FindIndex(l => l.Key == result.POC);

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

            // Find HVNs (High Volume Nodes) and compute Buy/Sell breakdown
            // HVN classification uses price position relative to close price (like NinjaTrader):
            // - HVNs at or below close = BuyHVNs (support levels where buyers accumulated)
            // - HVNs above close = SellHVNs (resistance levels where sellers accumulated)
            double hvnThreshold = maxVolume * hvnRatio;
            result.HVNs = new List<double>();
            result.HVNBuyCount = 0;
            result.HVNSellCount = 0;
            result.HVNBuyVolume = 0;
            result.HVNSellVolume = 0;

            // Use last close price for HVN classification, fallback to POC if not set
            double classificationPrice = _lastClosePrice > 0 ? _lastClosePrice : result.POC;

            foreach (var level in _volumeAtPrice)
            {
                if (level.Value.Volume >= hvnThreshold)
                {
                    result.HVNs.Add(level.Key);

                    // Classify HVN based on price position relative to close
                    // HVNs at or below close = BuyHVNs (support)
                    // HVNs above close = SellHVNs (resistance)
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
