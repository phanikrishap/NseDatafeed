using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Utility class for computing dynamic tick intervals based on price bands.
    /// Matches CustomOFVP.cs logic from NinjaTrader.
    /// </summary>
    public static class TickIntervalHelper
    {
        /// <summary>
        /// Gets the tick interval for volume profile bucketing based on current price.
        /// For futures (NIFTY_I, BANKNIFTY_I), uses fixed 1.0.
        /// For options, uses dynamic intervals based on price bands.
        /// </summary>
        /// <param name="price">Current price of the instrument</param>
        /// <param name="isFuture">True if instrument is a futures contract (ends with _I)</param>
        /// <returns>Tick interval for VP price bucketing</returns>
        public static double GetTickInterval(double price, bool isFuture)
        {
            if (isFuture)
                return 1.0;

            // Dynamic tick interval for options based on price bands
            // Matches CustomOFVP.cs GetTickInterval() logic
            if (price < 75)
                return 0.50;
            else if (price >= 75 && price < 125)
                return 0.75;
            else if (price >= 125 && price < 200)
                return 1.00;
            else if (price >= 200 && price < 300)
                return 2.00;
            else // price >= 300
                return 3.00;
        }
    }

    /// <summary>
    /// Stores a volume update for rolling window expiration.
    /// </summary>
    public class RollingVolumeUpdate
    {
        public double Price { get; set; }
        public double RoundedPrice { get; set; }
        public long Volume { get; set; }
        public bool IsBuy { get; set; }
        public long BuyVolume { get; set; }   // For split volume support (50/50 when price unchanged)
        public long SellVolume { get; set; }  // For split volume support
        public DateTime Time { get; set; }
        public double PriceSquaredVolume { get; set; }  // For StdDev calculation on expiration
    }

    /// <summary>
    /// Rolling Volume Profile Engine - maintains a time-windowed VP (60 minutes by default).
    /// Supports incremental updates with automatic expiration of old data.
    /// Supports both fixed and dynamic tick intervals for options.
    /// Computes SD bands matching OptionStratFinV1 granularity.
    /// </summary>
    public class RollingVolumeProfileEngine
    {
        private readonly Queue<RollingVolumeUpdate> _updates = new Queue<RollingVolumeUpdate>();
        private readonly Dictionary<double, VolumePriceLevel> _volumeAtPrice = new Dictionary<double, VolumePriceLevel>();
        private double _priceInterval = 1.0;
        private int _rollingWindowMinutes = 60;
        private double _totalVolume = 0;
        private double _sumPriceVolume = 0;
        private double _sumSquaredPriceVolume = 0;  // For StdDev calculation
        private double _lastClosePrice = 0;
        private bool _useDynamicInterval = false;

        public RollingVolumeProfileEngine(int rollingWindowMinutes = 60)
        {
            _rollingWindowMinutes = rollingWindowMinutes;
        }

        /// <summary>
        /// Reset with fixed price interval (for futures like NIFTY_I).
        /// </summary>
        public void Reset(double priceInterval)
        {
            _priceInterval = priceInterval > 0 ? priceInterval : 1.0;
            _useDynamicInterval = false;
            _volumeAtPrice.Clear();
            _updates.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
            _sumSquaredPriceVolume = 0;
        }

        /// <summary>
        /// Reset with dynamic tick interval mode (for options).
        /// Tick interval will be computed dynamically based on each tick's price band.
        /// </summary>
        public void ResetWithDynamicInterval()
        {
            _useDynamicInterval = true;
            _priceInterval = 0.50; // Default starting interval
            _volumeAtPrice.Clear();
            _updates.Clear();
            _totalVolume = 0;
            _sumPriceVolume = 0;
            _sumSquaredPriceVolume = 0;
        }

        /// <summary>
        /// Adds a tick to the rolling VP with timestamp for expiration tracking.
        /// Uses dynamic tick interval if enabled.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime tickTime)
        {
            if (price <= 0 || volume <= 0) return;

            // Get tick interval - dynamic for options, fixed for futures
            double interval = _useDynamicInterval
                ? TickIntervalHelper.GetTickInterval(price, false)
                : _priceInterval;

            double roundedPrice = Math.Round(price / interval) * interval;
            double priceSquaredVol = price * price * volume;

            // Store update for rolling window management
            _updates.Enqueue(new RollingVolumeUpdate
            {
                Price = price,
                RoundedPrice = roundedPrice,
                Volume = volume,
                IsBuy = isBuy,
                BuyVolume = isBuy ? volume : 0,
                SellVolume = isBuy ? 0 : volume,
                Time = tickTime,
                PriceSquaredVolume = priceSquaredVol
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
            _sumSquaredPriceVolume += priceSquaredVol;
        }

        /// <summary>
        /// Adds a tick with explicit buy/sell volume split.
        /// Used for NinjaTrader-style 50/50 split when price equals prior price.
        /// </summary>
        public void AddTickSplit(double price, long totalVolume, long buyVolume, long sellVolume, DateTime tickTime)
        {
            if (price <= 0 || totalVolume <= 0) return;

            double interval = _useDynamicInterval
                ? TickIntervalHelper.GetTickInterval(price, false)
                : _priceInterval;

            double roundedPrice = Math.Round(price / interval) * interval;
            double priceSquaredVol = price * price * totalVolume;

            // Store update for rolling window management
            _updates.Enqueue(new RollingVolumeUpdate
            {
                Price = price,
                RoundedPrice = roundedPrice,
                Volume = totalVolume,
                IsBuy = buyVolume >= sellVolume,  // For backward compat, not used in RemoveVolume when split
                BuyVolume = buyVolume,
                SellVolume = sellVolume,
                Time = tickTime,
                PriceSquaredVolume = priceSquaredVol
            });

            // Add to volume profile
            if (!_volumeAtPrice.ContainsKey(roundedPrice))
            {
                _volumeAtPrice[roundedPrice] = new VolumePriceLevel { Price = roundedPrice };
            }

            var level = _volumeAtPrice[roundedPrice];
            level.Volume += totalVolume;
            level.BuyVolume += buyVolume;
            level.SellVolume += sellVolume;

            _totalVolume += totalVolume;
            _sumPriceVolume += price * totalVolume;
            _sumSquaredPriceVolume += priceSquaredVol;
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
            // Use the rounded price that was stored when the tick was added
            if (!_volumeAtPrice.ContainsKey(update.RoundedPrice)) return;

            var level = _volumeAtPrice[update.RoundedPrice];
            level.Volume -= update.Volume;
            // Use stored buy/sell volumes to handle 50/50 split correctly
            level.BuyVolume -= update.BuyVolume;
            level.SellVolume -= update.SellVolume;

            _totalVolume -= update.Volume;
            _sumPriceVolume -= update.Price * update.Volume; // Use original price for VWAP
            _sumSquaredPriceVolume -= update.PriceSquaredVolume; // For StdDev

            // Remove price level if empty
            if (level.Volume <= 0)
            {
                _volumeAtPrice.Remove(update.RoundedPrice);
            }
        }

        public void SetClosePrice(double closePrice)
        {
            _lastClosePrice = closePrice;
        }

        public VPResult Calculate(double valueAreaPercent, double hvnRatio)
        {
            var result = new VPResult();

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

            // Calculate Standard Deviation and SD bands (matching VWAPWithStdDevBands indicator)
            if (_totalVolume > 0 && result.VWAP > 0)
            {
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
            }

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

        public RollingVolumeProfileEngine Clone()
        {
            var clone = new RollingVolumeProfileEngine(_rollingWindowMinutes);
            clone._priceInterval = this._priceInterval;
            clone._totalVolume = this._totalVolume;
            clone._sumPriceVolume = this._sumPriceVolume;
            clone._sumSquaredPriceVolume = this._sumSquaredPriceVolume;
            clone._lastClosePrice = this._lastClosePrice;
            clone._useDynamicInterval = this._useDynamicInterval;

            foreach (var update in _updates)
            {
                clone._updates.Enqueue(new RollingVolumeUpdate
                {
                    Price = update.Price,
                    RoundedPrice = update.RoundedPrice,
                    Volume = update.Volume,
                    IsBuy = update.IsBuy,
                    BuyVolume = update.BuyVolume,
                    SellVolume = update.SellVolume,
                    Time = update.Time,
                    PriceSquaredVolume = update.PriceSquaredVolume
                });
            }

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

        public void Restore(RollingVolumeProfileEngine other)
        {
            if (other == null) return;
            this._priceInterval = other._priceInterval;
            this._rollingWindowMinutes = other._rollingWindowMinutes;
            this._totalVolume = other._totalVolume;
            this._sumPriceVolume = other._sumPriceVolume;
            this._sumSquaredPriceVolume = other._sumSquaredPriceVolume;
            this._lastClosePrice = other._lastClosePrice;
            this._useDynamicInterval = other._useDynamicInterval;

            this._updates.Clear();
            foreach (var update in other._updates)
            {
                this._updates.Enqueue(new RollingVolumeUpdate
                {
                    Price = update.Price,
                    RoundedPrice = update.RoundedPrice,
                    Volume = update.Volume,
                    IsBuy = update.IsBuy,
                    BuyVolume = update.BuyVolume,
                    SellVolume = update.SellVolume,
                    Time = update.Time,
                    PriceSquaredVolume = update.PriceSquaredVolume
                });
            }

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
