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

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var value = factory(key);
            if (_entries.Count >= _capacity)
            {
                _entries.Remove(_order.Dequeue());
            }

            _entries.Add(key, value);
            _order.Enqueue(key);
            return value;
        }
    }
}