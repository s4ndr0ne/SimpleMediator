using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse> where TRequest : IRequest<TResponse>
{
    public override async Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handlerLease = ResolveHandler(serviceProvider);

        try
        {
            var handler = handlerLease.Handler;
            var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
            var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
            var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

            RequestHandlerDelegate<TResponse> handlerDelegate = async ct =>
            {
                foreach (var pre in preHandlers)
                {
                    await pre.Handle((TRequest)request, ct).ConfigureAwait(false);
                }

                var result = await handler.Handle((TRequest)request, ct).ConfigureAwait(false);

                foreach (var post in postHandlers)
                {
                    await post.Handle((TRequest)request, result, ct).ConfigureAwait(false);
                }

                return result;
            };

            // Build the behavior chain: lowest Order is outermost (runs first).
            var aggregate = behaviors
                .OrderByDescending(b => b.Order)
                .Aggregate(handlerDelegate, (next, behavior) => ct => behavior.Handle((TRequest)request, next, ct));

            try
            {
                return await aggregate(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is control flow, not an error to offer to exception handlers.
                throw;
            }
            catch (Exception exception)
            {
                // Exception handlers wrap the whole pipeline and run in ascending Order.
                var exceptionHandlers = serviceProvider
                    .GetServices<IRequestExceptionHandler<TRequest, TResponse>>()
                    .OrderBy(h => h.Order);
                var state = new RequestExceptionHandlerState<TResponse>();

                foreach (var exceptionHandler in exceptionHandlers)
                {
                    try
                    {
                        await exceptionHandler.Handle((TRequest)request, exception, state, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception handlerException)
                    {
                        throw new AggregateException(
                            "A request exception handler failed while handling the original request exception.",
                            exception,
                            handlerException);
                    }

                    if (state.Handled)
                    {
                        break;
                    }
                }

                if (state.Handled)
                {
                    return state.Response!;
                }

                throw;
            }
        }
        finally
        {
            // Closed handlers are owned and tracked by Microsoft DI. Open-generic
            // handlers are activated manually and are owned by this request.
            await handlerLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Resolves the single handler for this request, enforcing the one-handler rule across
    // both DI-registered (closed) handlers and on-demand-closed open-generic handlers.
    private static HandlerLease ResolveHandler(IServiceProvider serviceProvider)
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
            return new HandlerLease(closedHandler!);
        }

        // Exactly one open-generic match: build it via the cached factory, injecting its
        // dependencies from the current (scope-correct) provider. Open-generic request
        // handlers are created per request (transient) regardless of DefaultLifetime.
        var openGenericHandler = openMatches[0](serviceProvider, arguments: null);
        return new HandlerLease((IRequestHandler<TRequest, TResponse>)openGenericHandler, openGenericHandler);
    }

    private sealed class HandlerLease
    {
        private readonly object? _ownedInstance;

        public HandlerLease(IRequestHandler<TRequest, TResponse> handler, object? ownedInstance = null)
        {
            Handler = handler;
            _ownedInstance = ownedInstance;
        }

        public IRequestHandler<TRequest, TResponse> Handler { get; }

        public async ValueTask DisposeAsync()
        {
            switch (_ownedInstance)
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
