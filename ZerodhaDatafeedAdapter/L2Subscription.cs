using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace ZerodhaDatafeedAdapter;

/// <summary>
/// Thread-safe L2 (Market Depth) subscription container.
/// Refactored to match L1Subscription's thread-safe pattern.
/// </summary>
public class L2Subscription
{
    private readonly object _lock = new object();
    private readonly Dictionary<Instrument, L2CallbackEntry> _callbacks = new Dictionary<Instrument, L2CallbackEntry>();

    /// <summary>
    /// Gets the callback count (thread-safe)
    /// </summary>
    public int CallbackCount
    {
        get { lock (_lock) { return _callbacks.Count; } }
    }

    /// <summary>
    /// Checks if a callback exists for the given instrument (thread-safe)
    /// </summary>
    public bool ContainsCallback(Instrument instrument)
    {
        lock (_lock)
        {
            return _callbacks.ContainsKey(instrument);
        }
    }

    /// <summary>
    /// Adds or updates a callback for an instrument.
    /// Thread-safe operation.
    /// </summary>
    public void AddCallback(Instrument instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime> callback)
    {
        lock (_lock)
        {
            _callbacks[instrument] = new L2CallbackEntry
            {
                Instrument = instrument,
                Callback = callback,
                RegisteredAt = DateTime.UtcNow
            };
            Logger.Debug($"[L2Subscription] AddCallback: {instrument.FullName}, totalCallbacks={_callbacks.Count}");
        }
    }

    /// <summary>
    /// Removes a callback for an instrument.
    /// Thread-safe operation.
    /// </summary>
    public bool TryRemoveCallback(Instrument instrument)
    {
        lock (_lock)
        {
            bool removed = _callbacks.Remove(instrument);
            if (removed)
            {
                Logger.Debug($"[L2Subscription] RemovedCallback: {instrument.FullName}, remaining={_callbacks.Count}");
            }
            return removed;
        }
    }

    /// <summary>
    /// Gets a snapshot of all callbacks for safe iteration.
    /// Thread-safe operation.
    /// </summary>
    public List<KeyValuePair<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>> GetCallbacksSnapshot()
    {
        lock (_lock)
        {
            return _callbacks
                .Select(kvp => new KeyValuePair<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>(
                    kvp.Key, kvp.Value.Callback))
                .ToList();
        }
    }

    /// <summary>
    /// Clears all callbacks. Use on unsubscribe.
    /// Thread-safe operation.
    /// </summary>
    public void ClearAllCallbacks()
    {
        lock (_lock)
        {
            int count = _callbacks.Count;
            _callbacks.Clear();
            Logger.Info($"[L2Subscription] ClearAllCallbacks: Removed {count} callbacks");
        }
    }

    /// <summary>
    /// Legacy property for backward compatibility.
    /// WARNING: Direct access is deprecated. Use GetCallbacksSnapshot() for safe iteration.
    /// This property returns a snapshot copy to prevent mutation issues.
    /// </summary>
    [Obsolete("Use GetCallbacksSnapshot() instead for thread-safe iteration")]
    public SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>> L2Callbacks
    {
        get
        {
            lock (_lock)
            {
                var result = new SortedList<Instrument, Action<int, string, Operation, MarketDataType, double, long, DateTime>>();
                foreach (var kvp in _callbacks)
                {
                    result[kvp.Key] = kvp.Value.Callback;
                }
                return result;
            }
        }
        set
        {
            // Legacy setter - convert incoming SortedList to our internal dictionary
            lock (_lock)
            {
                _callbacks.Clear();
                if (value != null)
                {
                    foreach (var kvp in value)
                    {
                        _callbacks[kvp.Key] = new L2CallbackEntry
                        {
                            Instrument = kvp.Key,
                            Callback = kvp.Value,
                            RegisteredAt = DateTime.UtcNow
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// The NinjaTrader instrument for this subscription.
    /// This is read-only metadata set during subscription creation.
    /// </summary>
    public Instrument Instrument { get; set; }
}

/// <summary>
/// Represents a single L2 callback registration
/// </summary>
public class L2CallbackEntry
{
    public Instrument Instrument { get; set; }
    public Action<int, string, Operation, MarketDataType, double, long, DateTime> Callback { get; set; }
    public DateTime RegisteredAt { get; set; }
}
