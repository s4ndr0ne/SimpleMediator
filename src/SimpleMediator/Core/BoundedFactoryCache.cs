namespace SimpleMediator.Core;

internal sealed class BoundedFactoryCache<TKey, TValue> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _entries = new();
    private readonly Queue<TKey> _order = new();
    private readonly int _capacity;

    public BoundedFactoryCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // Do not run reflection/expression compilation while holding the global lock.
        // A concurrent miss may build the same value, but only one value is retained.
        if (TryGet(key, out var existing))
        {
            return existing;
        }

        var created = factory(key);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var retained))
            {
                return retained;
            }

            if (_entries.Count >= _capacity)
            {
                _entries.Remove(_order.Dequeue());
            }

            _entries.Add(key, created);
            _order.Enqueue(key);
            return created;
        }
    }

    private bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out value!);
        }
    }
}
