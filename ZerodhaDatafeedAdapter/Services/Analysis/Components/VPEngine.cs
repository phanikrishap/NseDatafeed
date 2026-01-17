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
    /// Result of VP calculation including HVN Buy/Sell breakdown and VWAP SD bands.
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

        // VWAP Standard Deviation Bands (matching OptionStratFinV1 granularity)
        public double StdDev { get; set; }
        public double Upper1SD { get; set; }    // VWAP + 1.0 * StdDev
        public double Upper1_5SD { get; set; }  // VWAP + 1.5 * StdDev
        public double Upper2SD { get; set; }    // VWAP + 2.0 * StdDev
        public double Upper2_5SD { get; set; }  // VWAP + 2.5 * StdDev
        public double Upper3SD { get; set; }    // VWAP + 3.0 * StdDev
        public double Lower1SD { get; set; }    // VWAP - 1.0 * StdDev
        public double Lower1_5SD { get; set; }  // VWAP - 1.5 * StdDev
        public double Lower2SD { get; set; }    // VWAP - 2.0 * StdDev
        public double Lower2_5SD { get; set; }  // VWAP - 2.5 * StdDev
        public double Lower3SD { get; set; }    // VWAP - 3.0 * StdDev

        public bool IsValid { get; set; }
    }

    /// <summary>
    /// Static helper to calculate VWAP location score based on price position relative to SD bands.
    /// Faithfully replicates OptionStratFinV1.CalculateVwapLocationScore() logic.
    /// </summary>
    public static class VWAPScoreCalculator
    {
        /// <summary>
        /// Calculates VWAP location score from -100 to +100 based on price position relative to SD bands.
        /// Matches OptionStratFinV1 scoring exactly:
        /// - Above VWAP: +20 (0-1SD), +40 (1SD), +50 (1.5SD), +70 (2SD), +85 (2.5SD), +100 (3SD+)
        /// - Below VWAP: -20 (0 to -1SD), -35 (-1SD), -65 (-1.5SD), -100 (-2SD and below)
        /// </summary>
        public static int CalculateScore(double currentPrice, VPResult vp)
        {
            if (vp == null || !vp.IsValid || double.IsNaN(currentPrice) || double.IsNaN(vp.VWAP) || vp.VWAP == 0)
                return 0;

            return CalculateScore(
                currentPrice,
                vp.VWAP,
                vp.Upper1SD, vp.Upper1_5SD, vp.Upper2SD, vp.Upper2_5SD, vp.Upper3SD,
                vp.Lower1SD, vp.Lower1_5SD, vp.Lower2SD, vp.Lower2_5SD, vp.Lower3SD);
        }

        /// <summary>
        /// Calculates VWAP location score with explicit band values.
        /// Faithfully replicates OptionStratFinV1.CalculateVwapLocationScore().
        /// </summary>
        public static int CalculateScore(
            double currentPrice,
            double vwap,
            double upper1SD,
            double upper1_5SD,
            double upper2SD,
            double upper2_5SD,
            double upper3SD,
            double lower1SD,
            double lower1_5SD,
            double lower2SD,
            double lower2_5SD,
            double lower3SD)
        {
            if (double.IsNaN(currentPrice) || double.IsNaN(vwap) || vwap == 0)
                return 0;

            if (currentPrice >= vwap)
            {
                // Upside scoring (matches OptionStratFinV1 exactly)
                if (currentPrice >= upper3SD)
                    return 100;
                else if (currentPrice >= upper2_5SD)
                    return 85;
                else if (currentPrice >= upper2SD)
                    return 70;
                else if (currentPrice >= upper1_5SD)
                    return 50;
                else if (currentPrice >= upper1SD)
                    return 40;
                else  // > vwap and < 1SD
                    return 20;
            }
            else
            {
                // Downside scoring (matches OptionStratFinV1 exactly)
                if (currentPrice <= lower2SD)
                    return -100;
                else if (currentPrice <= lower1_5SD)
                    return -65;
                else if (currentPrice <= lower1SD)
                    return -35;
                else  // < vwap and > -1SD
                    return -20;
            }
        }
    }

    /// <summary>
    /// Volume Profile Engine - Computes POC, VAH, VAL, VWAP, HVNs, and SD bands from tick data.
    /// Uses configurable price interval for price bucketing (1 rupee for NIFTY, not tick size).
    /// Value Area calculation uses 2-level expansion algorithm matching NinjaTrader/Gom.
    /// SD bands match OptionStratFinV1 granularity (1.0, 1.5, 2.0, 2.5, 3.0).
    /// </summary>
    public class VPEngine
    {
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;  // Price interval for VP buckets (1 rupee for NIFTY)
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;
        private double _sumSquaredPriceVolume = 0;  // For StdDev calculation

        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _volumeAtPrice.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
            _sumSquaredPriceVolume = 0;
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
            _sumSquaredPriceVolume += price * price * volume;  // For StdDev
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

            // Calculate Standard Deviation and SD bands (matching VWAPWithStdDevBands indicator)
            // Variance = E[X²] - E[X]² = (sumSquaredPriceVolume/totalVolume) - VWAP²
            double variance = (_sumSquaredPriceVolume / _totalVolume) - (result.VWAP * result.VWAP);
            result.StdDev = variance > 0 ? Math.Sqrt(variance) : 0;

            // Calculate all SD bands (matching OptionStratFinV1 granularity)
            result.Upper1SD = result.VWAP + (1.0 * result.StdDev);
            result.Upper1_5SD = result.VWAP + (1.5 * result.StdDev);
            result.Upper2SD = result.VWAP + (2.0 * result.StdDev);
            result.Upper2_5SD = result.VWAP + (2.5 * result.StdDev);
            result.Upper3SD = result.VWAP + (3.0 * result.StdDev);
            result.Lower1SD = result.VWAP - (1.0 * result.StdDev);
            result.Lower1_5SD = result.VWAP - (1.5 * result.StdDev);
            result.Lower2SD = result.VWAP - (2.0 * result.StdDev);
            result.Lower2_5SD = result.VWAP - (2.5 * result.StdDev);
            result.Lower3SD = result.VWAP - (3.0 * result.StdDev);

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

        public VPEngine Clone()
        {
            var clone = new VPEngine();
            clone._priceInterval = this._priceInterval;
            clone._totalVolume = this._totalVolume;
            clone._sumPriceVolume = this._sumPriceVolume;
            clone._sumSquaredPriceVolume = this._sumSquaredPriceVolume;
            clone._lastClosePrice = this._lastClosePrice;

            foreach (var kvp in _volumeAtPrice)
            {
                clone._volumeAtPrice[kvp.Key] = new VolumePriceLevel
                {
                    Price = kvp.Value.Price,
                    Volume = kvp.Value.Volume,
                    BuyVolume = kvp.Value.BuyVolume,
                    SellVolume = kvp.Value.SellVolume
                };
            }

            return clone;
        }

        public void Restore(VPEngine other)
        {
            if (other == null) return;
            this._priceInterval = other._priceInterval;
            this._totalVolume = other._totalVolume;
            this._sumPriceVolume = other._sumPriceVolume;
            this._sumSquaredPriceVolume = other._sumSquaredPriceVolume;
            this._lastClosePrice = other._lastClosePrice;

            this._volumeAtPrice.Clear();
            foreach (var kvp in other._volumeAtPrice)
            {
                this._volumeAtPrice[kvp.Key] = new VolumePriceLevel
                {
                    Price = kvp.Value.Price,
                    Volume = kvp.Value.Volume,
                    BuyVolume = kvp.Value.BuyVolume,
                    SellVolume = kvp.Value.SellVolume
                };
            }
        }
    }
}
