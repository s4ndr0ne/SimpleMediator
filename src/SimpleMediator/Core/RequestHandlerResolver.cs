using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal static class RequestHandlerResolver
{
    public static HandlerLease<TRequest, TResponse> Resolve<TRequest, TResponse>(IServiceProvider serviceProvider)
        where TRequest : IRequest<TResponse>
    {
        // Avoid materializing the DI result with ToList(). Requests normally have one
        // closed handler, so keeping only the first instance and a count saves a temporary
        // List allocation while preserving the multiple-handler validation. This path covers
        // closed handlers and native-compatible open-generic handlers registered in DI, so
        // lifetime and disposal follow the container. Only custom-mapped open generics fall
        // through to the factory path below.
        IRequestHandler<TRequest, TResponse>? closedHandler = null;
        var closedHandlerCount = 0;
        foreach (var candidate in serviceProvider.GetServices<IRequestHandler<TRequest, TResponse>>())
        {
            closedHandlerCount++;
            if (closedHandlerCount == 1)
            {
                closedHandler = candidate;
            }
        }

        var configuration = serviceProvider.GetService<MediatorConfiguration>();
        var openMatches = configuration is { CustomOpenGenericRequestHandlers.Count: > 0 }
            ? ResolveCustomOpenGeneric(configuration, typeof(TRequest), typeof(TResponse))
            : Array.Empty<OpenGenericHandlerFactory>();

        var total = closedHandlerCount + openMatches.Count;

        if (total == 0)
        {
            throw new RequestHandlerResolutionException(
                $"No request handler registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'.");
        }

        if (total > 1)
        {
            throw new RequestHandlerResolutionException(
                $"Multiple request handlers registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'. " +
                "A request can only have one handler.");
        }

        if (closedHandlerCount == 1)
        {
            return new HandlerLease<TRequest, TResponse>(closedHandler!);
        }

        // Exactly one custom-mapped open-generic match: resolve it through the lifetime store
        // that matches the configured lifetime. Transient instances belong to this request and are
        // disposed by the lease; scoped instances belong to the current DI scope; singleton
        // instances are always built from the ROOT provider so a handler can never capture a
        // request scope (see OpenGenericSingletonLifetimeStore).
        var match = openMatches[0];
        object openGenericHandler;
        object? ownedInstance = null;
        switch (match.Lifetime)
        {
            case ServiceLifetime.Singleton:
            {
                var store = serviceProvider.GetRequiredService<OpenGenericSingletonLifetimeStore>();
                openGenericHandler = store.GetOrAdd(
                    (match.RequestType, match.ResponseType, match.Factory),
                    () => match.Factory(store.RootProvider, arguments: null));
                break;
            }

            case ServiceLifetime.Scoped:
            {
                var store = serviceProvider.GetRequiredService<OpenGenericScopedLifetimeStore>();
                openGenericHandler = store.GetOrAdd(
                    (match.RequestType, match.ResponseType, match.Factory),
                    () => match.Factory(serviceProvider, arguments: null));
                break;
            }

            default:
                openGenericHandler = match.Factory(serviceProvider, arguments: null);
                ownedInstance = openGenericHandler;
                break;
        }

        return new HandlerLease<TRequest, TResponse>(
            (IRequestHandler<TRequest, TResponse>)openGenericHandler,
            ownedInstance);
    }

    // Custom-mapped open-generic handlers are only ever recorded by assembly scanning in the
    // reflection-based AddSimpleMediator, whose [RequiresUnreferencedCode]/[RequiresDynamicCode]
    // already warned the application at the composition root. Source-generated registrations close
    // those handlers at compile time and never populate the list.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = ReflectionWrapperFactory.Justification)]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = ReflectionWrapperFactory.Justification)]
    private static IReadOnlyList<OpenGenericHandlerFactory> ResolveCustomOpenGeneric(
        MediatorConfiguration configuration,
        Type requestType,
        Type responseType)
        => configuration.ResolveOpenGeneric(requestType, responseType).Factories;

    internal readonly struct HandlerLease<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly object? _ownedInstance;

        public HandlerLease(IRequestHandler<TRequest, TResponse> handler, object? ownedInstance = null)
        {
            Handler = handler;
            _ownedInstance = ownedInstance;
        }

        public IRequestHandler<TRequest, TResponse> Handler { get; }

        public ValueTask DisposeAsync()
        {
            switch (_ownedInstance)
            {
                case IAsyncDisposable asyncDisposable:
                    return asyncDisposable.DisposeAsync();
                case IDisposable disposable:
                    disposable.Dispose();
                    return default;
                default:
                    return default;
            }
        }
    }
}
