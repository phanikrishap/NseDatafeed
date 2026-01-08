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
    public class VPResult
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
    /// Volume Profile Engine - Computes POC, VAH, VAL, VWAP, HVNs from tick data.
    /// Uses configurable price interval for price bucketing (1 rupee for NIFTY, not tick size).
    /// Value Area calculation uses 2-level expansion algorithm matching NinjaTrader/Gom.
    /// </summary>
    public class VPEngine
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

        public VPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new VPResult();

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

            // Bidirectional expansion for Value Area using 2-level lookahead (matches NinjaTrader/Gom algorithm)
            var sortedLevels = _volumeAtPrice.OrderBy(l => l.Key).ToList();
            int pocIndex = sortedLevels.FindIndex(l => l.Key == result.POC);
            if (pocIndex < 0) pocIndex = sortedLevels.Count / 2;

            double targetVolume = _totalVolume * valueAreaPercent;
            double accumulatedVolume = maxVolume;

            int vahIndex = pocIndex;
            int valIndex = pocIndex;

            // Helper to get volume at index safely
            Func<int, double> getVol = (idx) =>
            {
                if (idx < 0 || idx >= sortedLevels.Count) return 0;
                return sortedLevels[idx].Value.Volume;
            };

            // Expand outward from POC, comparing 2 levels at a time (matches NinjaTrader/Gom algorithm)
            while (accumulatedVolume < targetVolume)
            {
                double upVolume = 0;
                double downVolume = 0;

                // Check 2 levels up
                if (vahIndex < sortedLevels.Count - 2)
                {
                    upVolume = getVol(vahIndex + 1) + getVol(vahIndex + 2);
                }
                else if (vahIndex < sortedLevels.Count - 1)
                {
                    upVolume = getVol(vahIndex + 1);
                }

                // Check 2 levels down
                if (valIndex > 1)
                {
                    downVolume = getVol(valIndex - 1) + getVol(valIndex - 2);
                }
                else if (valIndex > 0)
                {
                    downVolume = getVol(valIndex - 1);
                }

                if (upVolume == 0 && downVolume == 0)
                    break;

                if (upVolume >= downVolume && upVolume > 0)
                {
                    if (vahIndex < sortedLevels.Count - 2)
                    {
                        vahIndex += 2;
                        accumulatedVolume += upVolume;
                    }
                    else if (vahIndex < sortedLevels.Count - 1)
                    {
                        vahIndex += 1;
                        accumulatedVolume += upVolume;
                    }
                    else if (downVolume > 0)
                    {
                        if (valIndex > 1) { valIndex -= 2; accumulatedVolume += downVolume; }
                        else if (valIndex > 0) { valIndex -= 1; accumulatedVolume += downVolume; }
                    }
                    else break;
                }
                else if (downVolume > 0)
                {
                    if (valIndex > 1)
                    {
                        valIndex -= 2;
                        accumulatedVolume += downVolume;
                    }
                    else if (valIndex > 0)
                    {
                        valIndex -= 1;
                        accumulatedVolume += downVolume;
                    }
                    else if (upVolume > 0)
                    {
                        if (vahIndex < sortedLevels.Count - 2) { vahIndex += 2; accumulatedVolume += upVolume; }
                        else if (vahIndex < sortedLevels.Count - 1) { vahIndex += 1; accumulatedVolume += upVolume; }
                    }
                    else break;
                }
                else break;
            }

            result.VAH = sortedLevels[vahIndex].Key;
            result.VAL = sortedLevels[valIndex].Key;

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
