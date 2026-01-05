using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ZerodhaDatafeedAdapter.Helpers;

namespace ZerodhaDatafeedAdapter.Services.Analysis
{
    /// <summary>
    /// Centralized hub for real-time market data distribution.
    /// Manages current prices for underlyings and options.
    /// </summary>
    public class MarketDataHub
    {
        private static readonly Lazy<MarketDataHub> _instance = new Lazy<MarketDataHub>(() => new MarketDataHub());
        public static MarketDataHub Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, (double price, DateTime timestamp)> _priceHub = new ConcurrentDictionary<string, (double, DateTime)>();
        private readonly object _syncLock = new object();

        public event Action<string, double> PriceUpdated;

        private MarketDataHub() { }

        public void UpdatePrice(string symbol, double price, DateTime timestamp)
        {
            if (string.IsNullOrEmpty(symbol)) return;

            bool changed = false;
            if (_priceHub.TryGetValue(symbol, out var existing))
            {
                if (Math.Abs(existing.price - price) > 0.0001) changed = true;
            }
            else changed = true;

            if (changed)
            {
                _priceHub[symbol] = (price, timestamp);
                PriceUpdated?.Invoke(symbol, price);
            }
        }

        public void UpdatePrices(Dictionary<string, double> prices)
        {
            if (prices == null) return;
            foreach (var kvp in prices) UpdatePrice(kvp.Key, kvp.Value, DateTime.Now);
        }

        public double GetPrice(string symbol)
        {
            return _priceHub.TryGetValue(symbol, out var data) ? data.price : 0;
        }

        public (double price, DateTime timestamp) GetPriceWithTimestamp(string symbol)
        {
            return _priceHub.TryGetValue(symbol, out var data) ? data : (0, DateTime.MinValue);
        }

        public void Clear() => _priceHub.Clear();

        public void PruneStalePrices(TimeSpan maxAge)
        {
            var now = DateTime.Now;
            var staleKeys = _priceHub.Where(kvp => (now - kvp.Value.timestamp) > maxAge).Select(kvp => kvp.Key).ToList();
            foreach (var key in staleKeys) _priceHub.TryRemove(key, out _);
            if (staleKeys.Count > 0) Logger.Debug($"[Hub] Pruned {staleKeys.Count} stale prices.");
        }

        public Dictionary<string, double> GetAllPrices() => _priceHub.ToDictionary(k => k.Key, v => v.Value.price);
    }
}
