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
    public int ResolutionCacheCapacity { get; }
    public bool ValidationRequested { get; private set; }

    /// <summary>
    /// Open-generic request-handler implementation types that need custom type-argument
    /// inference (e.g. <c>EchoHandler&lt;T&gt; : IRequestHandler&lt;EchoRequest&lt;T&gt;, T&gt;</c>).
    /// Handlers compatible with native open-generic DI registration are registered directly
    /// in the service collection instead. See <see cref="OpenGenericMatcher"/>.
    /// </summary>
    public IReadOnlyList<Type> CustomOpenGenericRequestHandlers { get; }

    // Caches the *resolution plan* (matched closed types + compiled factories) per
    // request/response pair — never the handler instance, so scoped dependencies stay correct.
    private readonly BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution> _resolutionCache;
    internal readonly BoundedFactoryCache<(Type Request, Type Response), object> RequestHandlerWrappers;
    internal readonly BoundedFactoryCache<Type, object> NotificationHandlerWrappers;

    public MediatorConfiguration(
        NotificationPublishStrategy notificationPublishStrategy,
        IReadOnlyList<Type>? customOpenGenericRequestHandlers = null,
        int resolutionCacheCapacity = 1024,
        bool validationRequested = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolutionCacheCapacity);
        NotificationPublishStrategy = notificationPublishStrategy;
        ResolutionCacheCapacity = resolutionCacheCapacity;
        ValidationRequested = validationRequested;
        CustomOpenGenericRequestHandlers = customOpenGenericRequestHandlers ?? NoTypes;
        _resolutionCache = new BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution>(resolutionCacheCapacity);
        RequestHandlerWrappers = new BoundedFactoryCache<(Type Request, Type Response), object>(1024);
        NotificationHandlerWrappers = new BoundedFactoryCache<Type, object>(1024);
    }

    internal void RequestValidation() => ValidationRequested = true;

    /// <summary>
    /// Returns the cached set of open-generic handlers that match the given request/response
    /// pair, as pre-compiled object factories. Empty when none match.
    /// </summary>
    public OpenGenericResolution ResolveOpenGeneric(Type requestType, Type responseType)
        => _resolutionCache.GetOrAdd((requestType, responseType), key => BuildResolution(key.Request, key.Response));

    private OpenGenericResolution BuildResolution(Type requestType, Type responseType)
    {
        if (CustomOpenGenericRequestHandlers.Count == 0)
        {
            return OpenGenericResolution.Empty;
        }

        List<ObjectFactory>? factories = null;
        foreach (var openHandler in CustomOpenGenericRequestHandlers)
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
