using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ZerodhaDatafeedAdapter.Models.MarketData
{
    /// <summary>
    /// High-performance object pool for ZerodhaTickData to reduce GC pressure in hot paths.
    /// Uses a lock-free ConcurrentBag for thread-safe pooling.
    ///
    /// Usage:
    ///   var tickData = ZerodhaTickDataPool.Rent();
    ///   try {
    ///       // use tickData...
    ///   } finally {
    ///       ZerodhaTickDataPool.Return(tickData);
    ///   }
    /// </summary>
    public static class ZerodhaTickDataPool
    {
        // Pool configuration
        private const int MaxPoolSize = 256;  // Max objects to keep in pool
        private const int InitialPoolSize = 32;  // Pre-allocate on first access

        // Lock-free pool using ConcurrentBag
        private static readonly ConcurrentBag<ZerodhaTickData> _pool = new ConcurrentBag<ZerodhaTickData>();

        // Statistics for monitoring
        private static long _rented = 0;
        private static long _returned = 0;
        private static long _created = 0;
        private static long _dropped = 0;  // Objects not returned to pool (pool full)

        // Flag for one-time initialization
        private static int _initialized = 0;

        /// <summary>
        /// Rents a ZerodhaTickData instance from the pool.
        /// If the pool is empty, creates a new instance.
        /// The returned instance is reset and ready for use.
        /// </summary>
        public static ZerodhaTickData Rent()
        {
            // Lazy initialization of pool
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
            {
                Initialize();
            }

            Interlocked.Increment(ref _rented);

            if (_pool.TryTake(out var tickData))
            {
                // Got one from pool - it's already reset
                return tickData;
            }

            // Pool empty - create new instance
            Interlocked.Increment(ref _created);
            return new ZerodhaTickData();
        }

        /// <summary>
        /// Returns a ZerodhaTickData instance to the pool for reuse.
        /// The instance is reset before being added to the pool.
        /// </summary>
        public static void Return(ZerodhaTickData tickData)
        {
            if (tickData == null)
                return;

            Interlocked.Increment(ref _returned);

            // Reset the object for next use
            tickData.Reset();

            // Only add to pool if under max size (avoid unbounded growth)
            if (_pool.Count < MaxPoolSize)
            {
                _pool.Add(tickData);
            }
            else
            {
                // Pool is full - let GC collect this one
                Interlocked.Increment(ref _dropped);
            }
        }

        /// <summary>
        /// Pre-populates the pool with initial instances.
        /// Called once on first Rent().
        /// </summary>
        private static void Initialize()
        {
            for (int i = 0; i < InitialPoolSize; i++)
            {
                _pool.Add(new ZerodhaTickData());
                Interlocked.Increment(ref _created);
            }
        }

        /// <summary>
        /// Gets pool statistics for monitoring.
        /// </summary>
        public static (long rented, long returned, long created, long dropped, int poolSize) GetStats()
        {
            return (_rented, _returned, _created, _dropped, _pool.Count);
        }

        /// <summary>
        /// Clears the pool and resets statistics.
        /// Use only during shutdown or testing.
        /// </summary>
        public static void Clear()
        {
            while (_pool.TryTake(out _)) { }
            _rented = 0;
            _returned = 0;
            _created = 0;
            _dropped = 0;
            _initialized = 0;
        }
    }
}
