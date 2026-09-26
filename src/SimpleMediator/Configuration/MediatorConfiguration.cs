using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator;

namespace SimpleMediator.Configuration;

/// <summary>
/// Immutable runtime configuration resolved by the mediator core from DI.
/// Registered as a singleton by <c>AddSimpleMediator</c>.
/// </summary>
internal sealed class MediatorConfiguration
{
    private static readonly OpenGenericHandlerRegistration[] NoRegistrations = Array.Empty<OpenGenericHandlerRegistration>();

    public NotificationPublishStrategy NotificationPublishStrategy { get; }
    public int ResolutionCacheCapacity { get; }
    public bool ValidationRequested { get; private set; }

    /// <summary>
    /// When <c>true</c> (the default) the mediator refuses to be resolved from the root service
    /// provider. See <see cref="SimpleMediatorOptions.RequireScopedMediator"/>.
    /// </summary>
    public bool RequireScopedMediator { get; }

    /// <summary>
    /// Whether <see cref="RequireScopedMediator"/> was set explicitly by at least one
    /// <c>AddSimpleMediator</c> call, as opposed to being the default.
    /// </summary>
    public bool RequireScopedMediatorIsExplicit { get; }

    /// <summary>
    /// Open-generic request-handler implementation types that need custom type-argument
    /// inference (e.g. <c>EchoHandler&lt;T&gt; : IRequestHandler&lt;EchoRequest&lt;T&gt;, T&gt;</c>).
    /// Handlers compatible with native open-generic DI registration are registered directly
    /// in the service collection instead. See <see cref="OpenGenericMatcher"/>.
    /// </summary>
    public IReadOnlyList<OpenGenericHandlerRegistration> CustomOpenGenericRequestHandlers { get; }

    // Caches the *resolution plan* (matched closed types + compiled factories) per
    // request/response pair — never the handler instance, so scoped dependencies stay correct.
    private readonly BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution> _resolutionCache;
    internal readonly WrapperCache<(Type Request, Type Response)> RequestHandlerWrappers = new();
    internal readonly WrapperCache<Type> NotificationHandlerWrappers = new();

    public MediatorConfiguration(
        NotificationPublishStrategy notificationPublishStrategy,
        IReadOnlyList<OpenGenericHandlerRegistration>? customOpenGenericRequestHandlers = null,
        int resolutionCacheCapacity = 1024,
        bool validationRequested = false,
        bool requireScopedMediator = true,
        bool requireScopedMediatorIsExplicit = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolutionCacheCapacity);
        NotificationPublishStrategy = notificationPublishStrategy;
        ResolutionCacheCapacity = resolutionCacheCapacity;
        ValidationRequested = validationRequested;
        RequireScopedMediator = requireScopedMediator;
        RequireScopedMediatorIsExplicit = requireScopedMediatorIsExplicit;
        CustomOpenGenericRequestHandlers = customOpenGenericRequestHandlers ?? NoRegistrations;
        _resolutionCache = new BoundedFactoryCache<(Type Request, Type Response), OpenGenericResolution>(resolutionCacheCapacity);
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

        List<OpenGenericHandlerFactory>? factories = null;
        foreach (var registration in CustomOpenGenericRequestHandlers)
        {
            if (OpenGenericMatcher.TryClose(registration.ImplementationType, requestType, responseType, out var closedImplementation))
            {
                factories ??= new List<OpenGenericHandlerFactory>(1);
                factories.Add(new OpenGenericHandlerFactory(
                    ActivatorUtilities.CreateFactory(closedImplementation!, Type.EmptyTypes),
                    registration.Lifetime,
                    requestType,
                    responseType));
            }
        }

        return factories is null ? OpenGenericResolution.Empty : new OpenGenericResolution(factories);
    }
}

/// <summary>
/// A custom-mapped open-generic request handler implementation discovered by assembly scanning.
/// </summary>
/// <param name="ImplementationType">The open-generic implementation type.</param>
/// <param name="Lifetime">The configured <see cref="ServiceLifetime"/>.</param>
internal sealed record OpenGenericHandlerRegistration(Type ImplementationType, ServiceLifetime Lifetime);
