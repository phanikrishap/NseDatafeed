using System;
using System.Collections.Concurrent;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// VWAP data for a single instrument including standard deviation bands
    /// </summary>
    public class VWAPData
    {
        public string Symbol { get; set; }
        public double VWAP { get; set; }
        public double SD1Upper { get; set; }  // VWAP + 1 StdDev
        public double SD1Lower { get; set; }  // VWAP - 1 StdDev
        public double SD2Upper { get; set; }  // VWAP + 2 StdDev
        public double SD2Lower { get; set; }  // VWAP - 2 StdDev
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Returns position relative to VWAP:
        /// +2 = above SD2Upper, +1 = above SD1Upper, 0 = between bands, -1 = below SD1Lower, -2 = below SD2Lower
        /// </summary>
        public int GetPosition(double price)
        {
            if (price >= SD2Upper) return 2;
            if (price >= SD1Upper) return 1;
            if (price <= SD2Lower) return -2;
            if (price <= SD1Lower) return -1;
            return 0;
        }

        /// <summary>
        /// Distance from VWAP as percentage
        /// </summary>
        public double GetDistanceFromVWAP(double price)
        {
            if (VWAP == 0) return 0;
            return ((price - VWAP) / VWAP) * 100;
        }
    }

    /// <summary>
    /// Thread-safe shared cache for VWAP data computed by hidden indicators.
    /// This allows the Option Chain AddOn to access VWAP values calculated by indicators
    /// running on hidden charts or BarsRequests.
    /// </summary>
    public class VWAPDataCache
    {
        private static VWAPDataCache _instance;
        public static VWAPDataCache Instance => _instance ?? (_instance = new VWAPDataCache());

        // Symbol -> VWAPData mapping
        private readonly ConcurrentDictionary<string, VWAPData> _vwapData = new ConcurrentDictionary<string, VWAPData>();

        // Event fired when VWAP data is updated (symbol)
        public event Action<string, VWAPData> VWAPUpdated;

        private VWAPDataCache()
        {
            Logger.Info("[VWAPDataCache] Initialized");
        }

        /// <summary>
        /// Updates VWAP data for a symbol. Called by the hidden VWAP indicator.
        /// </summary>
        public void UpdateVWAP(string symbol, double vwap, double sd1Upper, double sd1Lower, double sd2Upper, double sd2Lower)
        {
            var data = new VWAPData
            {
                Symbol = symbol,
                VWAP = vwap,
                SD1Upper = sd1Upper,
                SD1Lower = sd1Lower,
                SD2Upper = sd2Upper,
                SD2Lower = sd2Lower,
                LastUpdated = DateTime.Now
            };

            _vwapData[symbol] = data;

            // Fire event for UI updates
            VWAPUpdated?.Invoke(symbol, data);

            if (Logger.IsDebugEnabled)
            {
                Logger.Debug($"[VWAPDataCache] Updated {symbol}: VWAP={vwap:F2}, SD1=[{sd1Lower:F2}-{sd1Upper:F2}], SD2=[{sd2Lower:F2}-{sd2Upper:F2}]");
            }
        }

        /// <summary>
        /// Gets VWAP data for a symbol, or null if not available
        /// </summary>
        public VWAPData GetVWAP(string symbol)
        {
            _vwapData.TryGetValue(symbol, out var data);
            return data;
        }

        /// <summary>
        /// Checks if VWAP data exists for a symbol
        /// </summary>
        public bool HasVWAP(string symbol)
        {
            return _vwapData.ContainsKey(symbol);
        }

        /// <summary>
        /// Removes VWAP data for a symbol (cleanup when unsubscribing)
        /// </summary>
        public void RemoveVWAP(string symbol)
        {
            _vwapData.TryRemove(symbol, out _);
        }

        /// <summary>
        /// Clears all cached VWAP data
        /// </summary>
        public void Clear()
        {
            _vwapData.Clear();
            Logger.Info("[VWAPDataCache] Cleared all VWAP data");
        }
    }
}
