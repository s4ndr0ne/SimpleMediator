using System.Collections.Concurrent;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// Unbounded, single-flight cache for the per-type dispatch wrappers.
/// </summary>
/// <remarks>
/// The key space is the set of request/notification types an application actually dispatches, which
/// is finite, so the cache is deliberately not bounded. A bounded FIFO cache used to evict hot
/// wrappers once more than its capacity of distinct types was in use, and every eviction forced the
/// wrapper to be rebuilt on the next call. The cache lives on the container's configuration
/// singleton, never in a static, so it is released with the container — the same lifetime for which
/// Microsoft DI itself retains the service types it has resolved.
/// </remarks>
internal sealed class WrapperCache<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<Type, object> _byPrimaryType = new();
    private readonly ConcurrentDictionary<TKey, object> _resolved = new();
    private readonly ConcurrentDictionary<TKey, Lazy<object>> _entries = new();

    internal int Count => _resolved.Count;

    public TWrapper GetOrAddByPrimaryType<TWrapper>(
        Type primaryType,
        TKey fullKey,
        Func<TKey, object> factory)
        where TWrapper : class
    {
        if (_byPrimaryType.TryGetValue(primaryType, out var existing) && existing is TWrapper typedExisting)
        {
            return typedExisting;
        }

        var resolved = GetOrAdd(fullKey, factory);
        _ = _byPrimaryType.TryAdd(primaryType, resolved);
        return (TWrapper)resolved;
    }

    public object GetOrAdd(TKey key, Func<TKey, object> factory)
    {
        ThrowHelper.ThrowIfNull(factory);

        if (_resolved.TryGetValue(key, out var resolved))
        {
            return resolved;
        }

        return GetOrAddSlow(key, factory);
    }

    private object GetOrAddSlow(TKey key, Func<TKey, object> factory)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            // Lazy keeps concurrent first use single-flight: only the winning entry's factory runs.
#if NETSTANDARD2_0
            entry = _entries.GetOrAdd(
                key,
                k => new Lazy<object>(() => factory(k), LazyThreadSafetyMode.ExecutionAndPublication));
#else
            entry = _entries.GetOrAdd(
                key,
                static (k, f) => new Lazy<object>(() => f(k), LazyThreadSafetyMode.ExecutionAndPublication),
                factory);
#endif
        }

        try
        {
            var value = entry.Value;
            _ = _resolved.TryAdd(key, value);
            return value;
        }
        catch
        {
            // Do not poison the cache with a faulted Lazy: remove this exact entry (a concurrent
            // replacement for the same key is preserved) so a later call can retry.
#if NETSTANDARD2_0
            _ = ((ICollection<KeyValuePair<TKey, Lazy<object>>>)_entries).Remove(new KeyValuePair<TKey, Lazy<object>>(key, entry));
#else
            _ = _entries.TryRemove(new KeyValuePair<TKey, Lazy<object>>(key, entry));
#endif
            throw;
        }
    }
}
