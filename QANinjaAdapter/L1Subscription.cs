using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace QANinjaAdapter;

/// <summary>
/// Thread-safe L1 subscription container supporting MULTIPLE callbacks per symbol.
/// NinjaTrader reuses the same Instrument object, so we use a unique callback ID
/// to track each subscriber (Chart, OptionChain, MarketAnalyzer, etc.)
/// </summary>
public class L1Subscription
{
    // Each callback is identified by a unique ID (generated from callback hash + timestamp)
    // This allows multiple subscribers using the same Instrument object
    private readonly ConcurrentDictionary<string, CallbackEntry> _callbacks
        = new ConcurrentDictionary<string, CallbackEntry>();

    // Keep legacy instrument-keyed dictionary for backward compatibility during tick processing
    // This maps Instrument -> list of callback IDs for that instrument
    private readonly ConcurrentDictionary<Instrument, List<string>> _instrumentToCallbackIds
        = new ConcurrentDictionary<Instrument, List<string>>();

    private readonly object _lock = new object();

    /// <summary>
    /// Gets the callback count (thread-safe)
    /// </summary>
    public int CallbackCount => _callbacks.Count;

    /// <summary>
    /// Checks if any callback exists for the given instrument (thread-safe)
    /// </summary>
    public bool ContainsCallback(Instrument instrument)
    {
        return _instrumentToCallbackIds.ContainsKey(instrument) &&
               _instrumentToCallbackIds[instrument].Count > 0;
    }

    /// <summary>
    /// Adds a callback for an instrument. Always adds (never replaces).
    /// Returns a unique callback ID that can be used to remove the callback later.
    /// Thread-safe operation.
    /// </summary>
    public string AddCallback(Instrument instrument, Action<MarketDataType, double, long, DateTime, long> callback)
    {
        lock (_lock)
        {
            // Generate unique ID for this callback
            string callbackId = $"{instrument.FullName}_{Guid.NewGuid():N}";

            var entry = new CallbackEntry
            {
                Id = callbackId,
                Instrument = instrument,
                Callback = callback,
                RegisteredAt = DateTime.UtcNow
            };

            _callbacks[callbackId] = entry;

            // Track which callbacks belong to this instrument
            if (!_instrumentToCallbackIds.TryGetValue(instrument, out var ids))
            {
                ids = new List<string>();
                _instrumentToCallbackIds[instrument] = ids;
            }
            ids.Add(callbackId);

            Logger.Debug($"[L1Subscription] AddCallback: {instrument.FullName}, callbackId={callbackId.Substring(callbackId.Length - 8)}, totalCallbacks={_callbacks.Count}");
            return callbackId;
        }
    }

    /// <summary>
    /// Tries to add a callback for an instrument. For backward compatibility.
    /// NOTE: This now ALWAYS adds and returns true (never returns false).
    /// Thread-safe operation.
    /// </summary>
    public bool TryAddCallback(Instrument instrument, Action<MarketDataType, double, long, DateTime, long> callback)
    {
        AddCallback(instrument, callback);
        return true; // Always succeeds now
    }

    /// <summary>
    /// Sets or updates a callback for an instrument.
    /// For backward compatibility - now just adds the callback.
    /// Thread-safe operation.
    /// </summary>
    public void SetCallback(Instrument instrument, Action<MarketDataType, double, long, DateTime, long> callback)
    {
        AddCallback(instrument, callback);
    }

    /// <summary>
    /// Removes a specific callback by ID.
    /// Thread-safe operation.
    /// </summary>
    public bool TryRemoveCallbackById(string callbackId)
    {
        lock (_lock)
        {
            if (_callbacks.TryRemove(callbackId, out var entry))
            {
                // Also remove from instrument mapping
                if (_instrumentToCallbackIds.TryGetValue(entry.Instrument, out var ids))
                {
                    ids.Remove(callbackId);
                    if (ids.Count == 0)
                    {
                        _instrumentToCallbackIds.TryRemove(entry.Instrument, out _);
                    }
                }
                Logger.Debug($"[L1Subscription] RemovedCallback: {entry.Instrument.FullName}, callbackId={callbackId.Substring(callbackId.Length - 8)}, remaining={_callbacks.Count}");
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes ALL callbacks for an instrument.
    /// Thread-safe operation.
    /// </summary>
    public bool TryRemoveCallback(Instrument instrument)
    {
        lock (_lock)
        {
            if (_instrumentToCallbackIds.TryRemove(instrument, out var ids))
            {
                foreach (var id in ids)
                {
                    _callbacks.TryRemove(id, out _);
                }
                Logger.Debug($"[L1Subscription] RemovedAllCallbacks for {instrument.FullName}, removed={ids.Count}, remaining={_callbacks.Count}");
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets a snapshot of all callbacks for safe iteration.
    /// Returns Instrument -> Callback pairs (flattened from our internal structure).
    /// Thread-safe operation.
    /// </summary>
    public List<KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>> GetCallbacksSnapshot()
    {
        lock (_lock)
        {
            return _callbacks.Values
                .Select(e => new KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>(
                    e.Instrument, e.Callback))
                .ToList();
        }
    }

    /// <summary>
    /// Gets callbacks as an enumerable for iteration.
    /// Note: For long iterations, prefer GetCallbacksSnapshot() to avoid potential issues.
    /// </summary>
    public IEnumerable<KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>> Callbacks
    {
        get
        {
            return _callbacks.Values
                .Select(e => new KeyValuePair<Instrument, Action<MarketDataType, double, long, DateTime, long>>(
                    e.Instrument, e.Callback));
        }
    }

    // Legacy property for backward compatibility
    // Returns a generated dictionary - this is expensive, use GetCallbacksSnapshot() instead
    [Obsolete("Use GetCallbacksSnapshot() instead for better performance")]
    public ConcurrentDictionary<Instrument, Action<MarketDataType, double, long, DateTime, long>> L1Callbacks
    {
        get
        {
            // For backward compatibility, return a dictionary
            // Note: This loses multiple callbacks per instrument - only returns one per instrument
            var result = new ConcurrentDictionary<Instrument, Action<MarketDataType, double, long, DateTime, long>>();
            foreach (var entry in _callbacks.Values)
            {
                result[entry.Instrument] = entry.Callback;
            }
            return result;
        }
    }

    // NOTE: PreviousVolume and PreviousPrice have been REMOVED from L1Subscription.
    // These are now tracked in OptimizedTickProcessor's shard-local SymbolState to prevent race conditions.
    // Each shard worker maintains its own SymbolState dictionary, providing single-writer guarantee.

    /// <summary>
    /// Cached flag for indices (GIFT NIFTY, NIFTY 50, SENSEX) - no volume, price updates only.
    /// This is read-only metadata set during subscription creation.
    /// </summary>
    public bool IsIndex { get; set; }

    /// <summary>
    /// The NinjaTrader instrument for this subscription.
    /// This is read-only metadata set during subscription creation.
    /// </summary>
    public Instrument Instrument { get; set; }
}

/// <summary>
/// Represents a single callback registration
/// </summary>
public class CallbackEntry
{
    public string Id { get; set; }
    public Instrument Instrument { get; set; }
    public Action<MarketDataType, double, long, DateTime, long> Callback { get; set; }
    public DateTime RegisteredAt { get; set; }
}
