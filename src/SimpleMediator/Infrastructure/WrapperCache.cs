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
    private readonly ConcurrentDictionary<TKey, Lazy<object>> _entries = new();

    internal int Count => _entries.Count;

    public object GetOrAdd(TKey key, Func<TKey, object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (!_entries.TryGetValue(key, out var entry))
        {
            // Lazy keeps concurrent first use single-flight: only the winning entry's factory runs.
            entry = _entries.GetOrAdd(
                key,
                static (k, f) => new Lazy<object>(() => f(k), LazyThreadSafetyMode.ExecutionAndPublication),
                factory);
        }

        try
        {
            return entry.Value;
        }
        catch
        {
            // Do not poison the cache with a faulted Lazy: remove this exact entry (a concurrent
            // replacement for the same key is preserved) so a later call can retry.
            _ = _entries.TryRemove(new KeyValuePair<TKey, Lazy<object>>(key, entry));
            throw;
        }
    }
}
