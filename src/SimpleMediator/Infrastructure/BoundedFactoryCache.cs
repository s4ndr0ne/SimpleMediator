using System.Collections.Concurrent;
using System.Threading;

namespace SimpleMediator.Core;

internal sealed class BoundedFactoryCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _entries = new();
    private readonly LinkedList<TKey> _order = new();
    private readonly int _capacity;
    private readonly object _evictionGate = new();

    public BoundedFactoryCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    // Exposed to the test assembly so the bounded-cache invariant can be asserted without
    // relying on reflection. This is not part of the public package API.
    internal int TrackedKeyCount
    {
        get
        {
            lock (_evictionGate)
            {
                return _order.Count;
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Lock-free read on hot path: TryGetValue on ConcurrentDictionary acquires zero locks.
        if (_entries.TryGetValue(key, out var existing))
        {
            return GetValueAndRemoveIfFaulted(key, existing);
        }

        var candidate = new CacheEntry(
            new Lazy<TValue>(() => factory(key), LazyThreadSafetyMode.ExecutionAndPublication));

        CacheEntry entry;
        lock (_evictionGate)
        {
            if (_entries.TryGetValue(key, out existing))
            {
                entry = existing;
            }
            else
            {
                entry = candidate;
                _ = _entries.TryAdd(key, entry);
                entry.Node = _order.AddLast(key);
                TrimToCapacity();
            }
        }

        // Evaluate outside the eviction gate. Lazy keeps concurrent first use single-flight,
        // while a faulted entry is removed only if it is still the current entry for the key.
        return GetValueAndRemoveIfFaulted(key, entry);
    }

    private void TrimToCapacity()
    {
        while (_entries.Count > _capacity)
        {
            var oldestNode = _order.First;
            if (oldestNode is null)
            {
                return;
            }

            _order.RemoveFirst();

            if (_entries.TryGetValue(oldestNode.Value, out var entry) &&
                ReferenceEquals(entry.Node, oldestNode))
            {
                _ = _entries.TryRemove(oldestNode.Value, out _);
                entry.Node = null;
            }
        }
    }

    private TValue GetValueAndRemoveIfFaulted(TKey key, CacheEntry entry)
    {
        try
        {
            return entry.Value.Value;
        }
        catch
        {
            // Do not poison the cache permanently when a transient factory failure occurs.
            // Remove the dictionary entry and its FIFO node as one operation. The identity
            // check preserves a concurrent replacement for the same key.
            lock (_evictionGate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _ = _entries.TryRemove(key, out _);
                    if (entry.Node is { } node)
                    {
                        _order.Remove(node);
                        entry.Node = null;
                    }
                }
            }

            throw;
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(Lazy<TValue> value) => Value = value;

        public Lazy<TValue> Value { get; }

        public LinkedListNode<TKey>? Node { get; set; }
    }
}
