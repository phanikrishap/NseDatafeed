using System;
using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Logging;

namespace ZerodhaDatafeedAdapter.Services.Analysis.Components
{
    /// <summary>
    /// Engine for building and managing composite volume profiles across multiple trading days.
    /// Stores daily session profiles and computes 1D, 3D, 5D, 10D composite profiles with ADR metrics.
    /// Uses VPEngine internally with 5.0 price interval for consistent VP calculations.
    /// </summary>
    public class CompositeProfileEngine
    {
        private const double COMPOSITE_PRICE_INTERVAL = 5.0;  // 5 rupee interval for composite (vs 1 for session)
        private const double VALUE_AREA_PERCENT = 0.70;
        private const double HVN_RATIO = 0.25;
        private const int HISTORICAL_ADR_LOOKBACK = 20;
        private const int YEARLY_BARS = 252;
        private const int SMOOTHING = 1;  // Gom-style smoothing parameter (1 recommended for composite profiles)

        // Storage for daily session profiles (last 10 days for composite, last 252 for yearly)
        private readonly List<DailySessionProfile> _dailyProfiles = new List<DailySessionProfile>();
        private readonly object _lock = new object();

        // Daily bar data (OHLC from 1440-minute bars) for range calculations
        private readonly List<DailyBarData> _dailyBars = new List<DailyBarData>();

        // Use VPEngine for current session VP calculation (5.0 interval)
        private readonly VPEngine _sessionVpEngine = new VPEngine();

        // Computed composite profiles
        private CompositeProfile _profile1D;
        private CompositeProfile _profile3D;
        private CompositeProfile _profile5D;
        private CompositeProfile _profile10D;

        // Current session state
        private DailySessionProfile _currentSession;
        private DateTime _currentSessionDate = DateTime.MinValue;
        private double _currentDayHigh = double.MinValue;
        private double _currentDayLow = double.MaxValue;

        // Computed metrics
        public CompositeProfileMetrics LatestMetrics { get; private set; } = new CompositeProfileMetrics();

        /// <summary>
        /// Gets the price interval used for composite profiles (5 rupees).
        /// </summary>
        public double PriceInterval => COMPOSITE_PRICE_INTERVAL;

        /// <summary>
        /// Gets the number of daily profiles stored.
        /// </summary>
        public int DailyProfileCount
        {
            get { lock (_lock) { return _dailyProfiles.Count; } }
        }

        /// <summary>
        /// Gets the number of daily bars stored.
        /// </summary>
        public int DailyBarCount
        {
            get { lock (_lock) { return _dailyBars.Count; } }
        }

        /// <summary>
        /// Adds a daily bar from the 1440-minute series.
        /// Used for range and ADR calculations.
        /// </summary>
        public void AddDailyBar(DateTime date, double open, double high, double low, double close, long volume)
        {
            lock (_lock)
            {
                // Check if we already have this date
                var existing = _dailyBars.FirstOrDefault(b => b.Date.Date == date.Date);
                if (existing != null)
                {
                    // Update existing bar
                    existing.Open = open;
                    existing.High = high;
                    existing.Low = low;
                    existing.Close = close;
                    existing.Volume = volume;
                    return;
                }

                // Add new bar
                _dailyBars.Add(new DailyBarData
                {
                    Date = date.Date,
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = volume
                });

                // Keep only YEARLY_BARS + buffer
                while (_dailyBars.Count > YEARLY_BARS + 10)
                {
                    _dailyBars.RemoveAt(0);
                }

                // Sort by date
                _dailyBars.Sort((a, b) => a.Date.CompareTo(b.Date));

                Logger.Debug($"[CompositeProfileEngine] Added daily bar: {date:yyyy-MM-dd}, H={high:F2}, L={low:F2}, Range={high - low:F2}");
            }
        }

        /// <summary>
        /// Starts a new session for the given date.
        /// Finalizes the previous session if it exists.
        /// </summary>
        public void StartSession(DateTime date)
        {
            lock (_lock)
            {
                // Finalize previous session
                if (_currentSession != null && _currentSession.PriceLadder.Count > 0)
                {
                    FinalizeCurrentSession();
                }

                // Reset the VP engine with 5.0 interval for this session
                _sessionVpEngine.Reset(COMPOSITE_PRICE_INTERVAL);

                // Start new session
                _currentSessionDate = date.Date;
                _currentSession = new DailySessionProfile
                {
                    Date = date.Date,
                    PriceLadder = new Dictionary<double, (long, long)>()
                };
                _currentDayHigh = double.MinValue;
                _currentDayLow = double.MaxValue;

                RangeBarLogger.Info($"[CompositeProfileEngine] Started new session: {date:yyyy-MM-dd}");
            }
        }

        /// <summary>
        /// Adds a tick to the current session's price ladder.
        /// Uses VPEngine with 5.0 price interval for consistent VP calculation.
        /// </summary>
        public void AddTick(double price, long volume, bool isBuy, DateTime time)
        {
            lock (_lock)
            {
                // Ensure we have a session for today
                if (_currentSession == null || time.Date != _currentSessionDate)
                {
                    StartSession(time);
                }

                // Feed tick to internal VP engine (handles price rounding to 5.0 interval internally)
                _sessionVpEngine.AddTick(price, volume, isBuy);

                // Also maintain our price ladder for composite aggregation
                double roundedPrice = Math.Round(price / COMPOSITE_PRICE_INTERVAL) * COMPOSITE_PRICE_INTERVAL;

                if (!_currentSession.PriceLadder.ContainsKey(roundedPrice))
                {
                    _currentSession.PriceLadder[roundedPrice] = (0, 0);
                }

                var (buyVol, sellVol) = _currentSession.PriceLadder[roundedPrice];
                if (isBuy)
                    _currentSession.PriceLadder[roundedPrice] = (buyVol + volume, sellVol);
                else
                    _currentSession.PriceLadder[roundedPrice] = (buyVol, sellVol + volume);

                // Track high/low
                if (price > _currentDayHigh) _currentDayHigh = price;
                if (price < _currentDayLow) _currentDayLow = price;

                _currentSession.High = _currentDayHigh;
                _currentSession.Low = _currentDayLow;
                _currentSession.Close = price;
                _currentSession.TotalVolume += volume;
            }
        }

        /// <summary>
        /// Finalizes the current session and stores it.
        /// Uses VPEngine to compute POC, VAH, VAL, HVNs.
        /// </summary>
        public void FinalizeCurrentSession()
        {
            lock (_lock)
            {
                if (_currentSession == null || _currentSession.PriceLadder.Count == 0)
                    return;

                // Use VPEngine for VP calculation (same logic as session VP)
                _sessionVpEngine.SetClosePrice(_currentSession.Close);
                var vpResult = _sessionVpEngine.Calculate(VALUE_AREA_PERCENT, HVN_RATIO);

                if (vpResult.IsValid)
                {
                    _currentSession.POC = vpResult.POC;
                    _currentSession.VAH = vpResult.VAH;
                    _currentSession.VAL = vpResult.VAL;
                    _currentSession.VWAP = vpResult.VWAP;
                    _currentSession.HVNs = vpResult.HVNs;
                    _currentSession.HVNBuyCount = vpResult.HVNBuyCount;
                    _currentSession.HVNSellCount = vpResult.HVNSellCount;
                    _currentSession.IsValid = true;
                }
                else
                {
                    // Fallback to manual calculation if VP engine fails
                    CalculateSessionMetrics(_currentSession);
                }

                // Add to daily profiles
                _dailyProfiles.Add(_currentSession);

                // Keep only last 10 days + buffer for composite calculations
                while (_dailyProfiles.Count > 15)
                {
                    _dailyProfiles.RemoveAt(0);
                }

                RangeBarLogger.Info($"[CompositeProfileEngine] Finalized session: {_currentSession.Date:yyyy-MM-dd}, " +
                    $"POC={_currentSession.POC:F2}, VAH={_currentSession.VAH:F2}, VAL={_currentSession.VAL:F2}, " +
                    $"Volume={_currentSession.TotalVolume}, PriceLevels={_currentSession.PriceLadder.Count}");

                _currentSession = null;
            }
        }

        /// <summary>
        /// Recalculates all composite profiles and metrics.
        /// Call this after processing ticks or on each bar close.
        /// </summary>
        public CompositeProfileMetrics Recalculate(double currentPrice, double currentDayHigh, double currentDayLow)
        {
            lock (_lock)
            {
                _currentDayHigh = currentDayHigh;
                _currentDayLow = currentDayLow;
                if (_currentSession != null)
                {
                    _currentSession.High = currentDayHigh;
                    _currentSession.Low = currentDayLow;
                    _currentSession.Close = currentPrice;
                }

                // Build composite profiles
                _profile1D = BuildCompositeProfile(1, currentPrice);
                _profile3D = BuildCompositeProfile(3, currentPrice);
                _profile5D = BuildCompositeProfile(5, currentPrice);
                _profile10D = BuildCompositeProfile(10, currentPrice);

                // Calculate metrics
                var metrics = new CompositeProfileMetrics
                {
                    CurrentPrice = currentPrice,
                    LastUpdate = DateTime.Now,
                    Symbol = "NIFTY_I",
                    DailyBarCount = _dailyBars.Count,
                    IsValid = _dailyBars.Count >= 3
                };

                // POC levels
                metrics.POC_1D = _profile1D?.POC ?? 0;
                metrics.POC_3D = _profile3D?.POC ?? 0;
                metrics.POC_5D = _profile5D?.POC ?? 0;
                metrics.POC_10D = _profile10D?.POC ?? 0;

                // VAH levels
                metrics.VAH_1D = _profile1D?.VAH ?? 0;
                metrics.VAH_3D = _profile3D?.VAH ?? 0;
                metrics.VAH_5D = _profile5D?.VAH ?? 0;
                metrics.VAH_10D = _profile10D?.VAH ?? 0;

                // VAL levels
                metrics.VAL_1D = _profile1D?.VAL ?? 0;
                metrics.VAL_3D = _profile3D?.VAL ?? 0;
                metrics.VAL_5D = _profile5D?.VAL ?? 0;
                metrics.VAL_10D = _profile10D?.VAL ?? 0;

                // Composite ranges (from daily bars, not profiles)
                CalculateCompositeRanges(metrics, currentDayHigh, currentDayLow);

                // ADR metrics
                CalculateADRMetrics(metrics);

                // Rolling ranges
                CalculateRollingRanges(metrics, currentDayHigh, currentDayLow);

                // Prior EOD ranges
                CalculatePriorEODRanges(metrics);

                // Yearly extremes
                CalculateYearlyExtremes(metrics, currentPrice, currentDayHigh, currentDayLow);

                // C vs Avg (percentage)
                if (metrics.ADR.Avg1DADR > 0)
                    metrics.CVsAvg_1D = (metrics.CompRange_1D / metrics.ADR.Avg1DADR) * 100;
                if (metrics.ADR.Avg3DADR > 0)
                    metrics.CVsAvg_3D = (metrics.CompRange_3D / metrics.ADR.Avg3DADR) * 100;
                if (metrics.ADR.Avg5DADR > 0)
                    metrics.CVsAvg_5D = (metrics.CompRange_5D / metrics.ADR.Avg5DADR) * 100;
                if (metrics.ADR.Avg10DADR > 0)
                    metrics.CVsAvg_10D = (metrics.CompRange_10D / metrics.ADR.Avg10DADR) * 100;

                // R vs Avg
                if (metrics.ADR.Avg1DADR > 0)
                    metrics.RVsAvg_1D = (metrics.RollRange_1D / metrics.ADR.Avg1DADR) * 100;
                if (metrics.ADR.Avg3DADR > 0)
                    metrics.RVsAvg_3D = (metrics.RollRange_3D / metrics.ADR.Avg3DADR) * 100;
                if (metrics.ADR.Avg5DADR > 0)
                    metrics.RVsAvg_5D = (metrics.RollRange_5D / metrics.ADR.Avg5DADR) * 100;
                if (metrics.ADR.Avg10DADR > 0)
                    metrics.RVsAvg_10D = (metrics.RollRange_10D / metrics.ADR.Avg10DADR) * 100;

                // Control and migration
                DetermineControlAndMigration(metrics);

                // Session date ranges for UI display
                metrics.DateRange1D = FormatDateRange(_profile1D);
                metrics.DateRange3D = FormatDateRange(_profile3D);
                metrics.DateRange5D = FormatDateRange(_profile5D);
                metrics.DateRange10D = FormatDateRange(_profile10D);

                LatestMetrics = metrics;
                return metrics;
            }
        }

        private CompositeProfile BuildCompositeProfile(int days, double currentClose)
        {
            var profile = new CompositeProfile { Days = days };

            // Get profiles to aggregate
            var profilesToUse = new List<DailySessionProfile>();

            // Include current session if it exists
            if (_currentSession != null && _currentSession.PriceLadder.Count > 0)
            {
                // Calculate current session metrics first
                CalculateSessionMetrics(_currentSession);
                profilesToUse.Add(_currentSession);
            }

            // Add finalized profiles (most recent first)
            var finalized = _dailyProfiles.OrderByDescending(p => p.Date).Take(days - profilesToUse.Count).ToList();
            profilesToUse.AddRange(finalized);

            if (profilesToUse.Count == 0)
                return profile;

            // Aggregate price ladders
            var aggregateLadder = new Dictionary<double, (long BuyVol, long SellVol)>();
            double totalVolume = 0;
            double vwapSum = 0;
            double profileHigh = double.MinValue;
            double profileLow = double.MaxValue;
            DateTime highDate = DateTime.MinValue;
            DateTime lowDate = DateTime.MinValue;

            foreach (var p in profilesToUse)
            {
                if (p.High > profileHigh)
                {
                    profileHigh = p.High;
                    highDate = p.Date;
                }
                if (p.Low < profileLow)
                {
                    profileLow = p.Low;
                    lowDate = p.Date;
                }

                foreach (var kvp in p.PriceLadder)
                {
                    if (!aggregateLadder.ContainsKey(kvp.Key))
                        aggregateLadder[kvp.Key] = (0, 0);

                    var (buy, sell) = aggregateLadder[kvp.Key];
                    aggregateLadder[kvp.Key] = (buy + kvp.Value.BuyVolume, sell + kvp.Value.SellVolume);

                    long vol = kvp.Value.BuyVolume + kvp.Value.SellVolume;
                    totalVolume += vol;
                    vwapSum += kvp.Key * vol;
                }
            }

            profile.AggregatePriceLadder = aggregateLadder;
            profile.TotalVolume = (long)totalVolume;
            profile.High = profileHigh;
            profile.Low = profileLow;
            profile.HighDate = highDate;
            profile.LowDate = lowDate;
            profile.StartDate = profilesToUse.Min(p => p.Date);
            profile.EndDate = profilesToUse.Max(p => p.Date);
            profile.VWAP = totalVolume > 0 ? vwapSum / totalVolume : 0;

            // Calculate POC, VAH, VAL using smoothed values
            if (aggregateLadder.Count > 0)
            {
                // Apply Gom-style triangular smoothing to the aggregate ladder
                var smoothedLadder = ApplyGomSmoothing(aggregateLadder);

                // POC - price with maximum smoothed volume
                var pocEntry = smoothedLadder.OrderByDescending(kvp => kvp.Value).First();
                profile.POC = pocEntry.Key;

                // Calculate Value Area using smoothed values
                CalculateValueAreaFromSmoothed(profile, smoothedLadder);

                // Calculate HVNs using smoothed values
                CalculateHVNsFromSmoothed(profile, smoothedLadder, currentClose);

                profile.IsValid = true;
            }

            return profile;
        }

        /// <summary>
        /// Applies Gom-style triangular smoothing to the price ladder.
        /// This spreads volume to neighboring price levels with decreasing weights.
        /// </summary>
        private Dictionary<double, double> ApplyGomSmoothing(Dictionary<double, (long BuyVol, long SellVol)> rawLadder)
        {
            var smoothed = new Dictionary<double, double>();

            if (SMOOTHING <= 0 || rawLadder.Count == 0)
            {
                // No smoothing - just convert to total volume
                foreach (var kvp in rawLadder)
                    smoothed[kvp.Key] = kvp.Value.BuyVol + kvp.Value.SellVol;
                return smoothed;
            }

            // Get sorted prices
            var sortedPrices = rawLadder.Keys.OrderBy(p => p).ToList();
            int maxIndex = sortedPrices.Count - 1;

            // Initialize smoothed values to 0
            foreach (var price in sortedPrices)
                smoothed[price] = 0;

            // Gom's smoothing algorithm (triangular weighted average)
            int paramSmooth = SMOOTHING + 1;

            if (paramSmooth % 2 != 0)
            {
                // Odd kernel size
                int num = paramSmooth / 2;
                double num2 = 1.0 / ((num + 1) * (num + 1));

                for (int i = 0; i <= maxIndex; i++)
                {
                    double price = sortedPrices[i];
                    double rawValue = rawLadder[price].BuyVol + rawLadder[price].SellVol;

                    // Distribute to neighbors with triangular weights
                    for (int j = 1; j <= num; j++)
                    {
                        double weight = rawValue * (num + 1 - j) * num2;

                        // Add to price + j intervals
                        int indexAbove = i + j;
                        if (indexAbove <= maxIndex)
                            smoothed[sortedPrices[indexAbove]] += weight;

                        // Add to price - j intervals
                        int indexBelow = i - j;
                        if (indexBelow >= 0)
                            smoothed[sortedPrices[indexBelow]] += weight;
                    }

                    // Center weight
                    double centerWeight = rawValue * (num + 1) * num2;
                    smoothed[price] += centerWeight;
                }
            }
            else
            {
                // Even kernel size (SMOOTHING=1 means paramSmooth=2, so this branch)
                int num5 = paramSmooth / 2 - 1;
                double num6 = 1.0 / ((num5 + 1) * (num5 + 1) + (num5 + 1));

                for (int i = 0; i <= maxIndex; i++)
                {
                    double price = sortedPrices[i];
                    double rawValue = rawLadder[price].BuyVol + rawLadder[price].SellVol;

                    for (int l = 0; l <= num5; l++)
                    {
                        double weight = rawValue * (num5 + 1 - l) * num6;

                        // Add to price + l + 1 intervals
                        int indexAbove = i + l + 1;
                        if (indexAbove <= maxIndex)
                            smoothed[sortedPrices[indexAbove]] += weight;

                        // Add to price - l intervals
                        int indexBelow = i - l;
                        if (indexBelow >= 0)
                            smoothed[sortedPrices[indexBelow]] += weight;
                    }
                }
            }

            return smoothed;
        }

        /// <summary>
        /// Calculates Value Area from smoothed price ladder using Gom's 2-level expansion algorithm.
        /// This matches NinjaTrader's implementation which expands 2 levels at a time from POC.
        /// </summary>
        private void CalculateValueAreaFromSmoothed(CompositeProfile profile, Dictionary<double, double> smoothedLadder)
        {
            var sortedPrices = smoothedLadder.Keys.OrderBy(p => p).ToList();
            double totalVolume = smoothedLadder.Values.Sum();
            double targetVolume = totalVolume * VALUE_AREA_PERCENT;

            int pocIndex = sortedPrices.IndexOf(profile.POC);
            if (pocIndex < 0)
            {
                // POC not found in sorted list, find closest
                pocIndex = sortedPrices.Count / 2;
            }

            double accumulatedVolume = smoothedLadder[sortedPrices[pocIndex]];
            int vahIndex = pocIndex;
            int valIndex = pocIndex;

            // Expand outward from POC, comparing 2 levels at a time (matches NinjaTrader/Gom algorithm)
            while (accumulatedVolume < targetVolume)
            {
                double upVolume = 0;
                double downVolume = 0;

                // Check 2 levels up
                if (vahIndex < sortedPrices.Count - 2)
                {
                    upVolume = smoothedLadder[sortedPrices[vahIndex + 1]] + smoothedLadder[sortedPrices[vahIndex + 2]];
                }
                else if (vahIndex < sortedPrices.Count - 1)
                {
                    upVolume = smoothedLadder[sortedPrices[vahIndex + 1]];
                }

                // Check 2 levels down
                if (valIndex > 1)
                {
                    downVolume = smoothedLadder[sortedPrices[valIndex - 1]] + smoothedLadder[sortedPrices[valIndex - 2]];
                }
                else if (valIndex > 0)
                {
                    downVolume = smoothedLadder[sortedPrices[valIndex - 1]];
                }

                if (upVolume == 0 && downVolume == 0)
                    break;

                if (upVolume >= downVolume && upVolume > 0)
                {
                    // Expand up
                    if (vahIndex < sortedPrices.Count - 2)
                    {
                        vahIndex += 2;
                        accumulatedVolume += upVolume;
                    }
                    else if (vahIndex < sortedPrices.Count - 1)
                    {
                        vahIndex += 1;
                        accumulatedVolume += upVolume;
                    }
                    else if (downVolume > 0)
                    {
                        // Can't go up, go down
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
                    }
                    else
                    {
                        break;
                    }
                }
                else if (downVolume > 0)
                {
                    // Expand down
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
                        // Can't go down, go up
                        if (vahIndex < sortedPrices.Count - 2)
                        {
                            vahIndex += 2;
                            accumulatedVolume += upVolume;
                        }
                        else if (vahIndex < sortedPrices.Count - 1)
                        {
                            vahIndex += 1;
                            accumulatedVolume += upVolume;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            profile.VAH = sortedPrices[vahIndex];
            profile.VAL = sortedPrices[valIndex];
        }

        /// <summary>
        /// Calculates HVNs from smoothed price ladder.
        /// </summary>
        private void CalculateHVNsFromSmoothed(CompositeProfile profile, Dictionary<double, double> smoothedLadder, double currentClose)
        {
            double pocVolume = smoothedLadder[profile.POC];
            double hvnThreshold = pocVolume * HVN_RATIO;

            profile.HVNs.Clear();
            profile.HVNBuyCount = 0;
            profile.HVNSellCount = 0;

            foreach (var kvp in smoothedLadder)
            {
                if (kvp.Value >= hvnThreshold)
                {
                    profile.HVNs.Add(kvp.Key);
                    if (kvp.Key <= currentClose)
                        profile.HVNBuyCount++;
                    else
                        profile.HVNSellCount++;
                }
            }
        }

        private void CalculateSessionMetrics(DailySessionProfile session)
        {
            if (session.PriceLadder.Count == 0)
                return;

            // Calculate POC
            var pocEntry = session.PriceLadder
                .OrderByDescending(kvp => kvp.Value.BuyVolume + kvp.Value.SellVolume)
                .First();
            session.POC = pocEntry.Key;

            // Calculate VWAP
            double vwapSum = 0;
            long totalVol = 0;
            foreach (var kvp in session.PriceLadder)
            {
                long vol = kvp.Value.BuyVolume + kvp.Value.SellVolume;
                vwapSum += kvp.Key * vol;
                totalVol += vol;
            }
            session.VWAP = totalVol > 0 ? vwapSum / totalVol : 0;

            // Calculate Value Area using 2-level expansion (matches NinjaTrader/Gom algorithm)
            var sortedPrices = session.PriceLadder.Keys.OrderBy(p => p).ToList();
            double totalVolume = session.PriceLadder.Values.Sum(v => v.BuyVolume + v.SellVolume);
            double targetVolume = totalVolume * VALUE_AREA_PERCENT;

            int pocIndex = sortedPrices.IndexOf(session.POC);
            if (pocIndex < 0) pocIndex = sortedPrices.Count / 2;

            double accumulatedVolume = session.PriceLadder[session.POC].BuyVolume + session.PriceLadder[session.POC].SellVolume;
            int vahIndex = pocIndex;
            int valIndex = pocIndex;

            // Helper to get volume at index
            Func<int, double> getVol = (idx) =>
            {
                if (idx < 0 || idx >= sortedPrices.Count) return 0;
                var entry = session.PriceLadder[sortedPrices[idx]];
                return entry.BuyVolume + entry.SellVolume;
            };

            while (accumulatedVolume < targetVolume)
            {
                double upVolume = 0;
                double downVolume = 0;

                // Check 2 levels up
                if (vahIndex < sortedPrices.Count - 2)
                {
                    upVolume = getVol(vahIndex + 1) + getVol(vahIndex + 2);
                }
                else if (vahIndex < sortedPrices.Count - 1)
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
                    if (vahIndex < sortedPrices.Count - 2)
                    {
                        vahIndex += 2;
                        accumulatedVolume += upVolume;
                    }
                    else if (vahIndex < sortedPrices.Count - 1)
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
                        if (vahIndex < sortedPrices.Count - 2) { vahIndex += 2; accumulatedVolume += upVolume; }
                        else if (vahIndex < sortedPrices.Count - 1) { vahIndex += 1; accumulatedVolume += upVolume; }
                    }
                    else break;
                }
                else break;
            }

            session.VAH = sortedPrices[vahIndex];
            session.VAL = sortedPrices[valIndex];

            // Calculate HVNs
            long pocVolume = session.PriceLadder[session.POC].BuyVolume + session.PriceLadder[session.POC].SellVolume;
            double hvnThreshold = pocVolume * HVN_RATIO;

            session.HVNs.Clear();
            session.HVNBuyCount = 0;
            session.HVNSellCount = 0;

            foreach (var kvp in session.PriceLadder)
            {
                long vol = kvp.Value.BuyVolume + kvp.Value.SellVolume;
                if (vol >= hvnThreshold)
                {
                    session.HVNs.Add(kvp.Key);
                    if (kvp.Key <= session.Close)
                        session.HVNBuyCount++;
                    else
                        session.HVNSellCount++;
                }
            }

            session.IsValid = true;
        }

        private void CalculateCompositeRanges(CompositeProfileMetrics metrics, double currentHigh, double currentLow)
        {
            // COMPOSITE RANGES: Fixed EOD ranges from prior N COMPLETED bars only (NOT including today)
            // This matches NinjaTrader FutBias behavior for "frozen" EOD composite metrics
            // Filter out today's bar - only use completed prior bars
            var today = DateTime.Today;
            var priorBars = _dailyBars
                .Where(b => b.Date.Date < today)
                .OrderByDescending(b => b.Date)
                .ToList();

            // 1D = yesterday's completed range (D-1)
            if (priorBars.Count >= 1)
            {
                metrics.CompRange_1D = priorBars[0].High - priorBars[0].Low;
            }

            // 3D composite range = prior 3 completed bars (D-1, D-2, D-3)
            if (priorBars.Count >= 3)
            {
                double high3D = priorBars.Take(3).Max(b => b.High);
                double low3D = priorBars.Take(3).Min(b => b.Low);
                metrics.CompRange_3D = high3D - low3D;
            }

            // 5D composite range = prior 5 completed bars (D-1 through D-5)
            if (priorBars.Count >= 5)
            {
                double high5D = priorBars.Take(5).Max(b => b.High);
                double low5D = priorBars.Take(5).Min(b => b.Low);
                metrics.CompRange_5D = high5D - low5D;
            }

            // 10D composite range = prior 10 completed bars (D-1 through D-10)
            if (priorBars.Count >= 10)
            {
                double high10D = priorBars.Take(10).Max(b => b.High);
                double low10D = priorBars.Take(10).Min(b => b.Low);
                metrics.CompRange_10D = high10D - low10D;
            }
        }

        private void CalculateADRMetrics(CompositeProfileMetrics metrics)
        {
            // ADR calculations use only prior COMPLETED bars (exclude today)
            var today = DateTime.Today;
            var priorBars = _dailyBars
                .Where(b => b.Date.Date < today)
                .OrderByDescending(b => b.Date)
                .ToList();

            // ADR values (from composite range metrics)
            metrics.ADR.Range1D = metrics.CompRange_1D;
            metrics.ADR.Range3D = metrics.CompRange_3D;
            metrics.ADR.Range5D = metrics.CompRange_5D;
            metrics.ADR.Range10D = metrics.CompRange_10D;

            // Calculate averages over past 20 periods using only completed bars
            if (priorBars.Count >= HISTORICAL_ADR_LOOKBACK)
            {
                // Avg 1D ADR = average of individual day ranges over 20 completed days
                metrics.ADR.Avg1DADR = priorBars.Take(HISTORICAL_ADR_LOOKBACK).Average(b => b.High - b.Low);

                // Avg 3D ADR = average of 3-day composite ranges over 20 periods
                var ranges3D = new List<double>();
                for (int i = 0; i <= priorBars.Count - 3 && ranges3D.Count < HISTORICAL_ADR_LOOKBACK; i++)
                {
                    var threeDays = priorBars.Skip(i).Take(3).ToList();
                    double high = threeDays.Max(b => b.High);
                    double low = threeDays.Min(b => b.Low);
                    ranges3D.Add(high - low);
                }
                if (ranges3D.Count > 0)
                    metrics.ADR.Avg3DADR = ranges3D.Average();

                // Avg 5D ADR
                var ranges5D = new List<double>();
                for (int i = 0; i <= priorBars.Count - 5 && ranges5D.Count < HISTORICAL_ADR_LOOKBACK; i++)
                {
                    var fiveDays = priorBars.Skip(i).Take(5).ToList();
                    double high = fiveDays.Max(b => b.High);
                    double low = fiveDays.Min(b => b.Low);
                    ranges5D.Add(high - low);
                }
                if (ranges5D.Count > 0)
                    metrics.ADR.Avg5DADR = ranges5D.Average();

                // Avg 10D ADR
                var ranges10D = new List<double>();
                for (int i = 0; i <= priorBars.Count - 10 && ranges10D.Count < HISTORICAL_ADR_LOOKBACK; i++)
                {
                    var tenDays = priorBars.Skip(i).Take(10).ToList();
                    double high = tenDays.Max(b => b.High);
                    double low = tenDays.Min(b => b.Low);
                    ranges10D.Add(high - low);
                }
                if (ranges10D.Count > 0)
                    metrics.ADR.Avg10DADR = ranges10D.Average();
            }
            else if (priorBars.Count > 0)
            {
                // Use available data
                metrics.ADR.Avg1DADR = priorBars.Average(b => b.High - b.Low);
            }
        }

        private void CalculateRollingRanges(CompositeProfileMetrics metrics, double currentHigh, double currentLow)
        {
            // ROLLING RANGES: Today's live high/low + N-1 prior COMPLETED bars
            // This updates in real-time as today's session progresses
            // Filter out today's bar - only use completed prior bars
            var today = DateTime.Today;
            var priorBars = _dailyBars
                .Where(b => b.Date.Date < today)
                .OrderByDescending(b => b.Date)
                .ToList();

            // Rolling 1D = today's live range only
            metrics.RollRange_1D = currentHigh > double.MinValue ? currentHigh - currentLow : 0;
            metrics.RollingRange.Rolling3DHigh = currentHigh;
            metrics.RollingRange.Rolling3DLow = currentLow;

            // Rolling 3D = today + 2 prior completed bars (D-1, D-2)
            if (priorBars.Count >= 2)
            {
                var prior2 = priorBars.Take(2).ToList();
                metrics.RollingRange.Rolling3DHigh = Math.Max(currentHigh, prior2.Max(b => b.High));
                metrics.RollingRange.Rolling3DLow = Math.Min(currentLow, prior2.Min(b => b.Low));
                metrics.RollRange_3D = metrics.RollingRange.Rolling3DHigh - metrics.RollingRange.Rolling3DLow;
                metrics.RollingRange.Rolling3DRange = metrics.RollRange_3D;
            }

            // Rolling 5D = today + 4 prior completed bars (D-1 through D-4)
            if (priorBars.Count >= 4)
            {
                var prior4 = priorBars.Take(4).ToList();
                metrics.RollingRange.Rolling5DHigh = Math.Max(currentHigh, prior4.Max(b => b.High));
                metrics.RollingRange.Rolling5DLow = Math.Min(currentLow, prior4.Min(b => b.Low));
                metrics.RollRange_5D = metrics.RollingRange.Rolling5DHigh - metrics.RollingRange.Rolling5DLow;
                metrics.RollingRange.Rolling5DRange = metrics.RollRange_5D;
            }

            // Rolling 10D = today + 9 prior completed bars (D-1 through D-9)
            if (priorBars.Count >= 9)
            {
                var prior9 = priorBars.Take(9).ToList();
                metrics.RollingRange.Rolling10DHigh = Math.Max(currentHigh, prior9.Max(b => b.High));
                metrics.RollingRange.Rolling10DLow = Math.Min(currentLow, prior9.Min(b => b.Low));
                metrics.RollRange_10D = metrics.RollingRange.Rolling10DHigh - metrics.RollingRange.Rolling10DLow;
                metrics.RollingRange.Rolling10DRange = metrics.RollRange_10D;
            }

            // Rolling vs Avg
            if (metrics.ADR.Avg3DADR > 0)
                metrics.RollingRange.Rolling3DVsAvg = metrics.RollRange_3D / metrics.ADR.Avg3DADR;
            if (metrics.ADR.Avg5DADR > 0)
                metrics.RollingRange.Rolling5DVsAvg = metrics.RollRange_5D / metrics.ADR.Avg5DADR;
            if (metrics.ADR.Avg10DADR > 0)
                metrics.RollingRange.Rolling10DVsAvg = metrics.RollRange_10D / metrics.ADR.Avg10DADR;
        }

        private void CalculatePriorEODRanges(CompositeProfileMetrics metrics)
        {
            // Prior EOD uses only completed bars (exclude today)
            var today = DateTime.Today;
            var priorBars = _dailyBars
                .Where(b => b.Date.Date < today)
                .OrderByDescending(b => b.Date)
                .ToList();

            // D-2: N-day composite ranges ending 2 days ago
            // Index 0 = D-1 (yesterday), index 1 = D-2, etc.
            if (priorBars.Count >= 2)
            {
                // D-2 1D range = just D-2's range
                metrics.D2_1DRange = priorBars[1].High - priorBars[1].Low;
                if (metrics.ADR.Avg1DADR > 0)
                    metrics.D2_1DVsAvg = (metrics.D2_1DRange / metrics.ADR.Avg1DADR) * 100;
            }

            // D-2 3D range = composite of D-2, D-3, D-4
            if (priorBars.Count >= 4)
            {
                var d2_3d = priorBars.Skip(1).Take(3).ToList();
                metrics.D2_3DRange = d2_3d.Max(b => b.High) - d2_3d.Min(b => b.Low);
                if (metrics.ADR.Avg3DADR > 0)
                    metrics.D2_3DVsAvg = (metrics.D2_3DRange / metrics.ADR.Avg3DADR) * 100;
            }

            // D-2 5D range = composite of D-2 through D-6
            if (priorBars.Count >= 6)
            {
                var d2_5d = priorBars.Skip(1).Take(5).ToList();
                metrics.D2_5DRange = d2_5d.Max(b => b.High) - d2_5d.Min(b => b.Low);
                if (metrics.ADR.Avg5DADR > 0)
                    metrics.D2_5DVsAvg = (metrics.D2_5DRange / metrics.ADR.Avg5DADR) * 100;
            }

            // D-2 10D range = composite of D-2 through D-11
            if (priorBars.Count >= 11)
            {
                var d2_10d = priorBars.Skip(1).Take(10).ToList();
                metrics.D2_10DRange = d2_10d.Max(b => b.High) - d2_10d.Min(b => b.Low);
                if (metrics.ADR.Avg10DADR > 0)
                    metrics.D2_10DVsAvg = (metrics.D2_10DRange / metrics.ADR.Avg10DADR) * 100;
            }

            // D-3: N-day composite ranges ending 3 days ago
            if (priorBars.Count >= 3)
            {
                metrics.D3_1DRange = priorBars[2].High - priorBars[2].Low;
                if (metrics.ADR.Avg1DADR > 0)
                    metrics.D3_1DVsAvg = (metrics.D3_1DRange / metrics.ADR.Avg1DADR) * 100;
            }

            if (priorBars.Count >= 5)
            {
                var d3_3d = priorBars.Skip(2).Take(3).ToList();
                metrics.D3_3DRange = d3_3d.Max(b => b.High) - d3_3d.Min(b => b.Low);
                if (metrics.ADR.Avg3DADR > 0)
                    metrics.D3_3DVsAvg = (metrics.D3_3DRange / metrics.ADR.Avg3DADR) * 100;
            }

            if (priorBars.Count >= 7)
            {
                var d3_5d = priorBars.Skip(2).Take(5).ToList();
                metrics.D3_5DRange = d3_5d.Max(b => b.High) - d3_5d.Min(b => b.Low);
                if (metrics.ADR.Avg5DADR > 0)
                    metrics.D3_5DVsAvg = (metrics.D3_5DRange / metrics.ADR.Avg5DADR) * 100;
            }

            if (priorBars.Count >= 12)
            {
                var d3_10d = priorBars.Skip(2).Take(10).ToList();
                metrics.D3_10DRange = d3_10d.Max(b => b.High) - d3_10d.Min(b => b.Low);
                if (metrics.ADR.Avg10DADR > 0)
                    metrics.D3_10DVsAvg = (metrics.D3_10DRange / metrics.ADR.Avg10DADR) * 100;
            }

            // D-4: N-day composite ranges ending 4 days ago
            if (priorBars.Count >= 4)
            {
                metrics.D4_1DRange = priorBars[3].High - priorBars[3].Low;
                if (metrics.ADR.Avg1DADR > 0)
                    metrics.D4_1DVsAvg = (metrics.D4_1DRange / metrics.ADR.Avg1DADR) * 100;
            }

            if (priorBars.Count >= 6)
            {
                var d4_3d = priorBars.Skip(3).Take(3).ToList();
                metrics.D4_3DRange = d4_3d.Max(b => b.High) - d4_3d.Min(b => b.Low);
                if (metrics.ADR.Avg3DADR > 0)
                    metrics.D4_3DVsAvg = (metrics.D4_3DRange / metrics.ADR.Avg3DADR) * 100;
            }

            if (priorBars.Count >= 8)
            {
                var d4_5d = priorBars.Skip(3).Take(5).ToList();
                metrics.D4_5DRange = d4_5d.Max(b => b.High) - d4_5d.Min(b => b.Low);
                if (metrics.ADR.Avg5DADR > 0)
                    metrics.D4_5DVsAvg = (metrics.D4_5DRange / metrics.ADR.Avg5DADR) * 100;
            }

            if (priorBars.Count >= 13)
            {
                var d4_10d = priorBars.Skip(3).Take(10).ToList();
                metrics.D4_10DRange = d4_10d.Max(b => b.High) - d4_10d.Min(b => b.Low);
                if (metrics.ADR.Avg10DADR > 0)
                    metrics.D4_10DVsAvg = (metrics.D4_10DRange / metrics.ADR.Avg10DADR) * 100;
            }
        }

        private void CalculateYearlyExtremes(CompositeProfileMetrics metrics, double currentPrice, double currentHigh, double currentLow)
        {
            var sortedBars = _dailyBars.OrderByDescending(b => b.Date).ToList();

            // Include current session
            double yearlyHigh = currentHigh > double.MinValue ? currentHigh : 0;
            double yearlyLow = currentLow < double.MaxValue ? currentLow : double.MaxValue;
            DateTime yearlyHighDate = DateTime.Today;
            DateTime yearlyLowDate = DateTime.Today;

            // Go through all bars
            foreach (var bar in sortedBars.Take(YEARLY_BARS))
            {
                if (bar.High > yearlyHigh)
                {
                    yearlyHigh = bar.High;
                    yearlyHighDate = bar.Date;
                }
                if (bar.Low < yearlyLow)
                {
                    yearlyLow = bar.Low;
                    yearlyLowDate = bar.Date;
                }
            }

            metrics.YearlyExtremes.YearlyHigh = yearlyHigh;
            metrics.YearlyExtremes.YearlyHighDate = yearlyHighDate;
            metrics.YearlyExtremes.YearlyLow = yearlyLow;
            metrics.YearlyExtremes.YearlyLowDate = yearlyLowDate;

            // Position in range
            double range = yearlyHigh - yearlyLow;
            if (range > 0)
                metrics.YearlyExtremes.PositionInRange = ((currentPrice - yearlyLow) / range) * 100;
        }

        /// <summary>
        /// Formats a date range for UI display.
        /// For single day: "13-Jan", for range: "10-Jan to 13-Jan"
        /// </summary>
        private string FormatDateRange(CompositeProfile profile)
        {
            if (profile == null || !profile.IsValid)
                return "-";

            if (profile.StartDate.Date == profile.EndDate.Date)
                return profile.StartDate.ToString("dd-MMM");

            return $"{profile.StartDate:dd-MMM} to {profile.EndDate:dd-MMM}";
        }

        private void DetermineControlAndMigration(CompositeProfileMetrics metrics)
        {
            // Simple control determination based on price position
            double price = metrics.CurrentPrice;

            if (_profile5D != null && _profile5D.IsValid && _profile3D != null && _profile3D.IsValid)
            {
                double poc5D = _profile5D.POC;
                double poc3D = _profile3D.POC;

                // Control: where is price relative to 5D value area
                if (price > _profile5D.VAH)
                    metrics.Control = "Buyer";
                else if (price < _profile5D.VAL)
                    metrics.Control = "Seller";
                else
                    metrics.Control = "Range";

                // Migration: compare 3D POC to 5D POC
                double pocDiff = poc3D - poc5D;
                if (Math.Abs(pocDiff) > 20) // Threshold for significant migration
                {
                    metrics.Migration = pocDiff > 0 ? "Up" : "Down";
                }
                else
                {
                    metrics.Migration = "None";
                }
            }
            else
            {
                metrics.Control = "N/A";
                metrics.Migration = "N/A";
            }
        }

        /// <summary>
        /// Resets the engine state.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _dailyProfiles.Clear();
                _dailyBars.Clear();
                _currentSession = null;
                _currentSessionDate = DateTime.MinValue;
                _currentDayHigh = double.MinValue;
                _currentDayLow = double.MaxValue;
                _profile1D = null;
                _profile3D = null;
                _profile5D = null;
                _profile10D = null;
                LatestMetrics = new CompositeProfileMetrics();
            }
        }

        /// <summary>
        /// Simple daily bar data container.
        /// </summary>
        public class DailyBarData
        {
            public DateTime Date { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public long Volume { get; set; }
        }
    }
}
