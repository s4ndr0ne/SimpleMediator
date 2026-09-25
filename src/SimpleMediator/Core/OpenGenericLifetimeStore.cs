using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator.Core;

/// <summary>
/// Caches one handler instance per closed (request, response) pair for a given lifetime.
/// Instances are always built from an explicitly supplied provider, never from an ambient one.
/// </summary>
internal abstract class OpenGenericLifetimeStore : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(Type Request, Type Response, ObjectFactory Factory), object> _instances = new();
    private bool _disposed;

    public object GetOrAdd(
        (Type Request, Type Response, ObjectFactory Factory) key,
        Func<object> factory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = factory();
            _instances.Add(key, created);
            return created;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        object[] instances;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            instances = _instances.Values.ToArray();
            _instances.Clear();
        }

        foreach (var instance in instances)
        {
            switch (instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}

/// <summary>
/// Holds custom-mapped open-generic request handlers for the lifetime of a DI scope.
/// </summary>
internal sealed class OpenGenericScopedLifetimeStore : OpenGenericLifetimeStore
{
}

/// <summary>
/// Holds custom-mapped open-generic request handlers for the lifetime of the application.
/// </summary>
/// <remarks>
/// Because the container injects the <em>root</em> provider into a singleton, this store owns the
/// root provider and uses it to build every handler. Passing the request scope's provider here is
/// what previously let a singleton handler capture — and keep alive — a scoped dependency whose
/// scope was later disposed. The registration validator additionally rejects singleton custom
/// handlers that declare a scoped or transient constructor dependency.
/// </remarks>
internal sealed class OpenGenericSingletonLifetimeStore : OpenGenericLifetimeStore
{
    public OpenGenericSingletonLifetimeStore(IServiceProvider rootProvider)
        => RootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));

    public IServiceProvider RootProvider { get; }
}
