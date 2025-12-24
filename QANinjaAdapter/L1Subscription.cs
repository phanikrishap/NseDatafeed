using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace QANinjaAdapter;

/// <summary>
/// Thread-safe L1 subscription container using ConcurrentDictionary for callbacks.
/// Supports concurrent reads during tick processing while allowing safe modifications
/// from NinjaTrader's subscription management threads.
/// </summary>
public class L1Subscription
{
    // Thread-safe collection that allows concurrent reads and writes
    private readonly ConcurrentDictionary<Instrument, Action<MarketDataType, double, long, DateTime, long>> _callbacks
        = new ConcurrentDictionary<Instrument, Action<MarketDataType, double, long, DateTime, long>>();

    /// <summary>
    /// Gets the callback count (thread-safe)
    /// </summary>
    public int CallbackCount => _callbacks.Count;

    /// <summary>
    /// Checks if a callback exists for the given instrument (thread-safe)
    /// </summary>
    public bool ContainsCallback(Instrument instrument) => _callbacks.ContainsKey(instrument);

    /// <summary>
    /// Tries to add a callback for an instrument. Returns false if already exists.
    /// Thread-safe operation.
    /// </summary>
    public bool TryAddCallback(Instrument instrument, Action<MarketDataType, double, long, DateTime, long> callback)
    {
        return _callbacks.TryAdd(instrument, callback);
    }

    /// <summary>
    /// Sets or updates a callback for an instrument.
    /// Thread-safe operation.
    /// </summary>
    public void SetCallback(Instrument instrument, Action<MarketDataType, double, long, DateTime, long> callback)
    {
        _callbacks[instrument] = callback;
    }

    /// <summary>
    /// Tries to remove a callback for an instrument.
    /// Thread-safe operation.
    /// </summary>
    public bool TryRemoveCallback(Instrument instrument)
    {
        return _callbacks.TryRemove(instrument, out _);
    }

    /// <summary>
    /// Gets a snapshot of all callbacks for safe iteration.
    /// This creates a copy so the caller can iterate without risk of "Collection was modified" exceptions.
    /// Thread-safe operation.
    /// </summary>
    public List<KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>> GetCallbacksSnapshot()
    {
        return _callbacks.ToList();
    }

    /// <summary>
    /// Gets callbacks as an enumerable for iteration.
    /// Note: For long iterations, prefer GetCallbacksSnapshot() to avoid potential issues.
    /// </summary>
    public IEnumerable<KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>> Callbacks => _callbacks;

    // Legacy property for backward compatibility during migration
    // Returns the underlying dictionary - use with caution, prefer thread-safe methods above
    [Obsolete("Use thread-safe methods (TryAddCallback, SetCallback, GetCallbacksSnapshot) instead")]
    public ConcurrentDictionary<Instrument, Action<MarketDataType, double, long, DateTime, long>> L1Callbacks => _callbacks;

    public int PreviousVolume { get; set; }
    public double PreviousPrice { get; set; }
    public bool IsIndex { get; set; }  // Cached flag for indices (GIFT NIFTY, NIFTY 50, SENSEX) - no volume, price updates only
    public Instrument Instrument { get; set; }
}
