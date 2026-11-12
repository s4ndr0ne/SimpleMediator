using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Infrastructure;

namespace SimpleMediator.Core;

/// <summary>
/// Caches one handler instance per closed (request, response) pair for a given lifetime.
/// Instances are always built from an explicitly supplied provider, never from an ambient one.
/// </summary>
internal abstract class OpenGenericLifetimeStore : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(Type Implementation, Type Request, Type Response), object> _instances = new();
    private Dictionary<(Type Implementation, Type Request, Type Response), Lazy<object>>? _inFlight;
    private bool _disposed;

    public object GetOrAdd(
        (Type Implementation, Type Request, Type Response) key,
        Func<object> factory)
    {
        lock (_gate)
        {
            ThrowHelper.ThrowIfDisposed(_disposed, this);
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        return GetOrAddSlow(
            key,
            () => new Lazy<object>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
    }

    public object GetOrAdd(
        (Type Implementation, Type Request, Type Response) key,
        ObjectFactory factory,
        IServiceProvider serviceProvider)
    {
        lock (_gate)
        {
            ThrowHelper.ThrowIfDisposed(_disposed, this);
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }
        }

        return GetOrAddSlow(
            key,
            () => new Lazy<object>(
                () => factory(serviceProvider, arguments: null),
                LazyThreadSafetyMode.ExecutionAndPublication));
    }

    private object GetOrAddSlow(
        (Type Implementation, Type Request, Type Response) key,
        Func<Lazy<object>> createLazy)
    {
        Lazy<object>? lazy;
        lock (_gate)
        {
            ThrowHelper.ThrowIfDisposed(_disposed, this);
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _inFlight ??= new Dictionary<(Type Implementation, Type Request, Type Response), Lazy<object>>();
            if (!_inFlight.TryGetValue(key, out lazy))
            {
                lazy = createLazy();
                _inFlight.Add(key, lazy);
            }
        }

        object created;
        try
        {
            created = lazy.Value;
        }
        catch
        {
            lock (_gate)
            {
                if (_inFlight is not null &&
                    _inFlight.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, lazy))
                {
                    _inFlight.Remove(key);
                }
            }

            throw;
        }

        var disposeOrphan = false;
        lock (_gate)
        {
            var removedInFlight = _inFlight is not null &&
                                  _inFlight.TryGetValue(key, out var current) &&
                                  ReferenceEquals(current, lazy) &&
                                  _inFlight.Remove(key);

            if (_disposed)
            {
                disposeOrphan = removedInFlight;
            }
            else
            {
                if (_instances.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                _instances.Add(key, created);
                return created;
            }
        }

        if (disposeOrphan)
        {
            DisposeOrphanInstance(created);
        }

        ThrowHelper.ThrowIfDisposed(true, this);
        return null!;
    }

    private static void DisposeOrphanInstance(object instance)
    {
        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
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

        List<Exception>? disposalExceptions = null;
        foreach (var instance in instances)
        {
            try
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
            catch (Exception exception)
            {
                (disposalExceptions ??= new List<Exception>()).Add(exception);
            }
        }

        if (disposalExceptions is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(disposalExceptions[0]).Throw();
        }

        if (disposalExceptions is { Count: > 1 })
        {
            throw new AggregateException("One or more open-generic request handlers failed during disposal.", disposalExceptions);
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
