using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaDatafeedAdapter.SyntheticInstruments
{
    /// <summary>
    /// Smart caching strategy for straddle states with LRU eviction and memory pressure handling
    /// </summary>
    public class StraddleCacheManager : IDisposable
    {
        // LRU Cache implementation
        private readonly ConcurrentDictionary<string, CacheNode> _cache;
        private readonly LinkedList<string> _accessOrder;
        private readonly object _accessOrderLock = new object();
        
        // Memory pressure monitoring
        private readonly Timer _evictionTimer;
        private volatile bool _isUnderMemoryPressure = false;
        
        // Configuration
        private int _maxCacheSize;
        private readonly int _normalMaxSize;
        private readonly int _pressureMaxSize;
        private readonly TimeSpan _maxAge;
        private readonly TimeSpan _evictionInterval;
        
        // Performance metrics
        private long _cacheHits = 0;
        private long _cacheMisses = 0;
        private long _evictions = 0;
        private long _timeBasedEvictions = 0;
        private long _memoryPressureEvictions = 0;
        
        private volatile bool _isDisposed = false;
        
        public StraddleCacheManager(
            int normalMaxSize = 1000,
            int pressureMaxSize = 200,
            TimeSpan? maxAge = null,
            TimeSpan? evictionInterval = null)
        {
            _normalMaxSize = normalMaxSize;
            _pressureMaxSize = pressureMaxSize;
            _maxCacheSize = normalMaxSize;
            _maxAge = maxAge ?? TimeSpan.FromMinutes(10); // Default: 10 minutes
            _evictionInterval = evictionInterval ?? TimeSpan.FromMinutes(1); // Check every minute
            
            _cache = new ConcurrentDictionary<string, CacheNode>();
            _accessOrder = new LinkedList<string>();
            
            // Start eviction timer
            _evictionTimer = new Timer(PerformEviction, null, _evictionInterval, _evictionInterval);
            
            Logger.Info($"[StraddleCacheManager] Started with max size: {_normalMaxSize}, max age: {_maxAge}");
        }
        
        /// <summary>
        /// Get straddle state from cache with LRU access tracking
        /// </summary>
        public StraddleState GetStraddleState(string straddleSymbol)
        {
            if (string.IsNullOrEmpty(straddleSymbol) || _isDisposed)
                return null;
            
            if (_cache.TryGetValue(straddleSymbol, out var node))
            {
                // Update access time and order
                node.LastAccessTime = DateTime.UtcNow;
                UpdateAccessOrder(straddleSymbol);
                
                Interlocked.Increment(ref _cacheHits);
                return node.State;
            }
            
            Interlocked.Increment(ref _cacheMisses);
            return null;
        }
        
        /// <summary>
        /// Put straddle state into cache with automatic eviction if needed
        /// </summary>
        public void PutStraddleState(string straddleSymbol, StraddleState state)
        {
            if (string.IsNullOrEmpty(straddleSymbol) || state == null || _isDisposed)
                return;
            
            var now = DateTime.UtcNow;
            var node = new CacheNode
            {
                State = state,
                CreationTime = now,
                LastAccessTime = now
            };
            
            // Add or update cache entry
            _cache.AddOrUpdate(straddleSymbol, node, (key, existing) =>
            {
                existing.State = state;
                existing.LastAccessTime = now;
                return existing;
            });
            
            // Update access order
            UpdateAccessOrder(straddleSymbol);
            
            // Check if eviction is needed
            CheckAndEvictIfNeeded();
        }
        
        /// <summary>
        /// Remove specific straddle from cache
        /// </summary>
        public bool RemoveStraddleState(string straddleSymbol)
        {
            if (string.IsNullOrEmpty(straddleSymbol) || _isDisposed)
                return false;
            
            bool removed = _cache.TryRemove(straddleSymbol, out _);
            
            if (removed)
            {
                RemoveFromAccessOrder(straddleSymbol);
                Interlocked.Increment(ref _evictions);
            }
            
            return removed;
        }
        
        /// <summary>
        /// Update LRU access order for a straddle symbol
        /// </summary>
        private void UpdateAccessOrder(string straddleSymbol)
        {
            lock (_accessOrderLock)
            {
                // Remove from current position if exists
                _accessOrder.Remove(straddleSymbol);
                
                // Add to front (most recently used)
                _accessOrder.AddFirst(straddleSymbol);
            }
        }
        
        /// <summary>
        /// Remove from access order tracking
        /// </summary>
        private void RemoveFromAccessOrder(string straddleSymbol)
        {
            lock (_accessOrderLock)
            {
                _accessOrder.Remove(straddleSymbol);
            }
        }
        
        /// <summary>
        /// Check memory pressure and evict if cache is over limit
        /// </summary>
        private void CheckAndEvictIfNeeded()
        {
            // Simple memory pressure detection
            long currentMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
            _isUnderMemoryPressure = currentMemoryMB > 500; // Over 500MB is pressure
            
            // Adjust cache size based on memory pressure
            int targetSize = _isUnderMemoryPressure ? _pressureMaxSize : _normalMaxSize;
            _maxCacheSize = targetSize;
            
            // Evict if over limit
            int currentSize = _cache.Count;
            if (currentSize > _maxCacheSize)
            {
                int evictCount = currentSize - _maxCacheSize;
                EvictLeastRecentlyUsed(evictCount);
                
                if (_isUnderMemoryPressure)
                {
                    Interlocked.Add(ref _memoryPressureEvictions, evictCount);
                    Logger.Warn($"[StraddleCacheManager] Memory pressure eviction: {evictCount} items removed (Memory: {currentMemoryMB}MB)");
                }
            }
        }
        
        /// <summary>
        /// Evict least recently used items from cache
        /// </summary>
        private void EvictLeastRecentlyUsed(int count)
        {
            var toEvict = new List<string>();
            
            lock (_accessOrderLock)
            {
                var current = _accessOrder.Last;
                int evicted = 0;
                
                while (current != null && evicted < count)
                {
                    toEvict.Add(current.Value);
                    current = current.Previous;
                    evicted++;
                }
            }
            
            // Remove from cache and access order
            foreach (var symbol in toEvict)
            {
                if (_cache.TryRemove(symbol, out _))
                {
                    RemoveFromAccessOrder(symbol);
                    Interlocked.Increment(ref _evictions);
                }
            }
        }
        
        /// <summary>
        /// Periodic eviction based on time and memory pressure
        /// </summary>
        private void PerformEviction(object state)
        {
            if (_isDisposed) return;
            
            try
            {
                var now = DateTime.UtcNow;
                var expiredItems = new List<string>();
                
                // Find expired items
                foreach (var kvp in _cache)
                {
                    var age = now - kvp.Value.LastAccessTime;
                    if (age > _maxAge)
                    {
                        expiredItems.Add(kvp.Key);
                    }
                }
                
                // Remove expired items
                foreach (var symbol in expiredItems)
                {
                    if (_cache.TryRemove(symbol, out _))
                    {
                        RemoveFromAccessOrder(symbol);
                        Interlocked.Increment(ref _timeBasedEvictions);
                    }
                }
                
                if (expiredItems.Count > 0)
                {
                    Logger.Debug($"[StraddleCacheManager] Time-based eviction: {expiredItems.Count} expired items removed");
                }
                
                // Emergency cleanup during severe memory pressure
                long currentMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                if (currentMemoryMB > 800) // Severe pressure above 800MB
                {
                    // Aggressive eviction - remove 50% of cache
                    int aggressiveEvictCount = _cache.Count / 2;
                    if (aggressiveEvictCount > 0)
                    {
                        EvictLeastRecentlyUsed(aggressiveEvictCount);
                        Logger.Warn($"[StraddleCacheManager] Emergency eviction: {aggressiveEvictCount} items removed due to severe memory pressure ({currentMemoryMB}MB)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleCacheManager] Error during eviction: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear all cached straddle states
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            
            lock (_accessOrderLock)
            {
                _accessOrder.Clear();
            }
            
            Logger.Info("[StraddleCacheManager] Cache cleared");
        }
        
        /// <summary>
        /// Get comprehensive cache statistics
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            var totalRequests = _cacheHits + _cacheMisses;
            var hitRate = totalRequests > 0 ? (double)_cacheHits / totalRequests : 0.0;
            
            return new CacheStatistics
            {
                CacheSize = _cache.Count,
                MaxCacheSize = _maxCacheSize,
                CacheHits = _cacheHits,
                CacheMisses = _cacheMisses,
                HitRate = hitRate,
                TotalEvictions = _evictions,
                TimeBasedEvictions = _timeBasedEvictions,
                MemoryPressureEvictions = _memoryPressureEvictions,
                IsUnderMemoryPressure = _isUnderMemoryPressure,
                MaxAge = _maxAge,
                CurrentMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024
            };
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            try
            {
                _evictionTimer?.Dispose();
                Clear();
                Logger.Info("[StraddleCacheManager] Disposed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[StraddleCacheManager] Error during disposal: {ex.Message}");
            }
        }
    }
    
    internal class CacheNode
    {
        public StraddleState State { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastAccessTime { get; set; }
    }
    
    public class CacheStatistics
    {
        public int CacheSize { get; set; }
        public int MaxCacheSize { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public double HitRate { get; set; }
        public long TotalEvictions { get; set; }
        public long TimeBasedEvictions { get; set; }
        public long MemoryPressureEvictions { get; set; }
        public bool IsUnderMemoryPressure { get; set; }
        public TimeSpan MaxAge { get; set; }
        public long CurrentMemoryMB { get; set; }
        
        public override string ToString()
        {
            return $"Cache: {CacheSize}/{MaxCacheSize} ({HitRate:P1} hit rate), " +
                   $"Evictions: {TotalEvictions} (Time: {TimeBasedEvictions}, Memory: {MemoryPressureEvictions})";
        }
    }
}
