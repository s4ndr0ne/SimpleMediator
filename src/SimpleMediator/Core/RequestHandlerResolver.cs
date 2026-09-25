using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal static class RequestHandlerResolver
{
    public static HandlerLease<TRequest, TResponse> Resolve<TRequest, TResponse>(IServiceProvider serviceProvider)
        where TRequest : IRequest<TResponse>
    {
        // Avoid materializing the DI result with ToList(). Requests normally have one
        // closed handler, so keeping only the first instance and a count saves a temporary
        // List allocation while preserving the multiple-handler validation.
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
        var openMatches = configuration?.ResolveOpenGeneric(typeof(TRequest), typeof(TResponse)).Factories
                          ?? Array.Empty<ObjectFactory>();

        var total = closedHandlerCount + openMatches.Count;

        if (total == 0)
        {
            throw new InvalidOperationException(
                $"No request handler registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'.");
        }

        if (total > 1)
        {
            throw new InvalidOperationException(
                $"Multiple request handlers registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'. " +
                "A request can only have one handler.");
        }

        if (closedHandlerCount == 1)
        {
            return new HandlerLease<TRequest, TResponse>(closedHandler!);
        }

        // Exactly one open-generic match: build it via the cached factory, injecting its
        // dependencies from the current (scope-correct) provider. Open-generic request
        // handlers are created per request (transient) regardless of DefaultLifetime.
        var openGenericHandler = openMatches[0](serviceProvider, arguments: null);
        return new HandlerLease<TRequest, TResponse>((IRequestHandler<TRequest, TResponse>)openGenericHandler, openGenericHandler);
    }

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
                    return ValueTask.CompletedTask;
                default:
                    return ValueTask.CompletedTask;
            }
        }
    }
}
