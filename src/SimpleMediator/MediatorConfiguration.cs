using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<(Type Request, Type Response), OpenGenericResolution> _resolutionCache = new();

    public MediatorConfiguration(
        NotificationPublishStrategy notificationPublishStrategy,
        IReadOnlyList<Type>? openGenericRequestHandlers = null)
    {
        NotificationPublishStrategy = notificationPublishStrategy;
        OpenGenericRequestHandlers = openGenericRequestHandlers ?? NoTypes;
    }

    /// <summary>
    /// Returns the cached set of open-generic handlers that match the given request/response
    /// pair, as pre-compiled object factories. Empty when none match.
    /// </summary>
    public OpenGenericResolution ResolveOpenGeneric(Type requestType, Type responseType)
        => _resolutionCache.GetOrAdd((requestType, responseType), key => BuildResolution(key.Request, key.Response));

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
