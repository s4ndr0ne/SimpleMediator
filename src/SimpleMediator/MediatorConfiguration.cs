using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;

namespace SimpleMediator;

/// <summary>
/// Immutable runtime configuration resolved by the mediator core from DI.
/// Registered as a singleton by <c>AddSimpleMediator</c>.
/// </summary>
internal sealed class MediatorConfiguration
{
    private static readonly Type[] NoTypes = Array.Empty<Type>();

    public NotificationPublishStrategy NotificationPublishStrategy { get; }

    /// <summary>
    /// Open-generic request-handler implementation types discovered by assembly scanning
    /// (e.g. <c>EchoHandler&lt;&gt;</c>). These cannot be registered with Microsoft DI and
    /// are closed on demand by the request wrapper. See <see cref="OpenGenericMatcher"/>.
    /// </summary>
    public IReadOnlyList<Type> OpenGenericRequestHandlers { get; }

    // Caches the *resolution plan* (matched closed types + compiled factories) per
    // request/response pair — never the handler instance, so scoped dependencies stay correct.
    private readonly BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution> _resolutionCache;
    internal readonly BoundedFactoryCache<(Type Request, Type Response), object> RequestHandlerWrappers;
    internal readonly BoundedFactoryCache<Type, object> NotificationHandlerWrappers;
    private readonly BoundedFactoryCache<(Type Request, Type Response), int[]> _behaviorOrderCache;
    private readonly BoundedFactoryCache<(Type Request, Type Response), int[]> _exceptionHandlerOrderCache;

    public MediatorConfiguration(
        NotificationPublishStrategy notificationPublishStrategy,
        IReadOnlyList<Type>? openGenericRequestHandlers = null,
        int resolutionCacheCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolutionCacheCapacity);
        NotificationPublishStrategy = notificationPublishStrategy;
        OpenGenericRequestHandlers = openGenericRequestHandlers ?? NoTypes;
        _resolutionCache = new BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution>(resolutionCacheCapacity);
        RequestHandlerWrappers = new BoundedFactoryCache<(Type Request, Type Response), object>(resolutionCacheCapacity);
        NotificationHandlerWrappers = new BoundedFactoryCache<Type, object>(resolutionCacheCapacity);
        _behaviorOrderCache = new BoundedFactoryCache<(Type Request, Type Response), int[]>(resolutionCacheCapacity);
        _exceptionHandlerOrderCache = new BoundedFactoryCache<(Type Request, Type Response), int[]>(resolutionCacheCapacity);
    }

    /// <summary>
    /// Returns the cached set of open-generic handlers that match the given request/response
    /// pair, as pre-compiled object factories. Empty when none match.
    /// </summary>
    public OpenGenericResolution ResolveOpenGeneric(Type requestType, Type responseType)
        => _resolutionCache.GetOrAdd((requestType, responseType), key => BuildResolution(key.Request, key.Response));

    internal int[] GetBehaviorOrder<T>(Type requestType, Type responseType, IReadOnlyList<T> behaviors, Func<T, int> order)
        => _behaviorOrderCache.GetOrAdd((requestType, responseType), _ => BuildOrder(behaviors, order, descending: true));

    internal int[] GetExceptionHandlerOrder<T>(Type requestType, Type responseType, IReadOnlyList<T> handlers, Func<T, int> order)
        => _exceptionHandlerOrderCache.GetOrAdd((requestType, responseType), _ => BuildOrder(handlers, order, descending: false));

    private static int[] BuildOrder<T>(IReadOnlyList<T> values, Func<T, int> order, bool descending)
    {
        var indexes = Enumerable.Range(0, values.Count).ToArray();
        Array.Sort(indexes, (left, right) => descending
            ? order(values[right]).CompareTo(order(values[left]))
            : order(values[left]).CompareTo(order(values[right])));
        return indexes;
    }

    private OpenGenericResolution BuildResolution(Type requestType, Type responseType)
    {
        if (OpenGenericRequestHandlers.Count == 0)
        {
            return OpenGenericResolution.Empty;
        }

        List<ObjectFactory>? factories = null;
        foreach (var openHandler in OpenGenericRequestHandlers)
        {
            if (OpenGenericMatcher.TryClose(openHandler, requestType, responseType, out var closedImplementation))
            {
                factories ??= new List<ObjectFactory>(1);
                factories.Add(ActivatorUtilities.CreateFactory(closedImplementation!, Type.EmptyTypes));
            }
        }

        return factories is null ? OpenGenericResolution.Empty : new OpenGenericResolution(factories);
    }
}
