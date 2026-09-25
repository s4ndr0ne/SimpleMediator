using System.Collections.Concurrent;
using System.Threading;

namespace SimpleMediator.Core;

internal sealed class BoundedFactoryCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<TValue>> _entries = new();
    private readonly ConcurrentQueue<TKey> _order = new();
    private readonly int _capacity;
    private readonly object _evictionGate = new();

    public BoundedFactoryCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Lock-free read on hot path: TryGetValue on ConcurrentDictionary acquires zero locks.
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing.Value;
        }

        var candidate = new Lazy<TValue>(() => factory(key), LazyThreadSafetyMode.ExecutionAndPublication);

        if (_entries.TryAdd(key, candidate))
        {
            _order.Enqueue(key);

            if (_entries.Count > _capacity)
            {
                lock (_evictionGate)
                {
                    while (_entries.Count > _capacity && _order.TryDequeue(out var oldest))
                    {
                        _entries.TryRemove(oldest, out _);
                    }
                }
            }

            return GetValueAndRemoveIfFaulted(key, candidate);
        }

        return _entries.TryGetValue(key, out existing)
            ? GetValueAndRemoveIfFaulted(key, existing)
            : GetValueAndRemoveIfFaulted(key, candidate);
    }

    private TValue GetValueAndRemoveIfFaulted(TKey key, Lazy<TValue> entry)
    {
        try
        {
            return entry.Value;
        }
        catch
        {
            // Do not poison the cache permanently when a transient factory failure occurs.
            // Remove only this exact Lazy instance so a concurrent replacement is preserved.
            ((ICollection<KeyValuePair<TKey, Lazy<TValue>>>)_entries)
                .Remove(new KeyValuePair<TKey, Lazy<TValue>>(key, entry));
            throw;
        }
    }
}
