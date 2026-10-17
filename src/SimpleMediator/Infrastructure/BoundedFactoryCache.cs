using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SimpleMediator.Infrastructure;

// Lazy<T> declares DynamicallyAccessedMembers(PublicParameterlessConstructor) on T for its
// default constructor; the annotation is propagated so trimming keeps the requirement visible.
internal sealed class BoundedFactoryCache<TKey, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _entries = new();
    // Recency order for eviction: nodes are moved to the tail on use (see Touch), so the head is
    // always the least recently used entry.
    private readonly LinkedList<TKey> _order = new();
    private readonly int _capacity;
    private readonly object _evictionGate = new();

    public BoundedFactoryCache(int capacity)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);

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
        ThrowHelper.ThrowIfNull(factory);

        // Read path stays lock-free: TryGetValue on ConcurrentDictionary acquires zero locks.
        // A hit additionally refreshes the entry's recency so eviction under capacity pressure
        // takes the least recently used entry rather than the oldest inserted one — with plain
        // FIFO the entries inserted first are usually the ones dispatched most, so they were
        // evicted first and their (possibly expensive) factory re-ran on the next call.
        if (_entries.TryGetValue(key, out var existing))
        {
            Touch(existing);
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

    // Refreshes the recency of a hot entry without ever blocking the read path: the gate is
    // taken opportunistically (zero timeout) and a contended refresh is simply skipped — the
    // entry keeps its current position, which only makes eviction slightly more conservative.
    private void Touch(CacheEntry entry)
    {
        if (!Monitor.TryEnter(_evictionGate, 0))
        {
            return;
        }

        try
        {
            // Re-check membership under the gate: the entry may have been evicted between the
            // lock-free read and acquiring it, in which case its node is already detached.
            if (entry.Node is { } node &&
                ReferenceEquals(node.List, _order) &&
                !ReferenceEquals(_order.Last, node))
            {
                _order.Remove(node);
                _order.AddLast(node);
            }
        }
        finally
        {
            Monitor.Exit(_evictionGate);
        }
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
