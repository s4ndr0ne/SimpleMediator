using System.Collections.Concurrent;

namespace SimpleMediator.Core;

internal sealed class BoundedFactoryCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _entries = new();
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
            return existing;
        }

        var created = factory(key);

        if (_entries.TryAdd(key, created))
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

            return created;
        }

        return _entries.TryGetValue(key, out existing) ? existing : created;
    }
}
