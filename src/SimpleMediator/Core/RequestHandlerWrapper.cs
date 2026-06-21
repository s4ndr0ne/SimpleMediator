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
        var handler = ResolveHandler(serviceProvider);

        var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
        var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

        RequestHandlerDelegate<TResponse> handlerDelegate = async (ct) =>
        {
            foreach (var pre in preHandlers)
            {
                await pre.Handle((TRequest)request, ct);
            }

            var result = await handler.Handle((TRequest)request, ct);

            foreach (var post in postHandlers)
            {
                await post.Handle((TRequest)request, result, ct);
            }

            return result;
        };

        // Build the behavior chain: lowest Order is outermost (runs first).
        var aggregate = behaviors
            .OrderByDescending(b => b.Order)
            .Aggregate(handlerDelegate, (next, behavior) => ct => behavior.Handle((TRequest)request, next, ct));

        try
        {
            return await aggregate(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation on our token must propagate — never let an exception
            // handler turn it into a "successful" response.
            throw;
        }
        catch (Exception exception)
        {
            // Give exception handlers a chance to recover. They wrap the whole pipeline
            // (behaviors + pre/post + handler) and run in ascending Order; the first to
            // SetHandled wins.
            var exceptionHandlers = serviceProvider
                .GetServices<IRequestExceptionHandler<TRequest, TResponse>>()
                .OrderBy(h => h.Order);
            var state = new RequestExceptionHandlerState<TResponse>();

            foreach (var exceptionHandler in exceptionHandlers)
            {
                await exceptionHandler.Handle((TRequest)request, exception, state, cancellationToken);
                if (state.Handled)
                {
                    break;
                }
            }

            if (state.Handled)
            {
                return state.Response!;
            }

            throw; // preserves the original stack trace
        }
    }

    // Resolves the single handler for this request, enforcing the one-handler rule across
    // both DI-registered (closed) handlers and on-demand-closed open-generic handlers.
    private static IRequestHandler<TRequest, TResponse> ResolveHandler(IServiceProvider serviceProvider)
    {
        var closedHandlers = serviceProvider.GetServices<IRequestHandler<TRequest, TResponse>>().ToList();

        var configuration = serviceProvider.GetService<MediatorConfiguration>();
        var openMatches = configuration?.ResolveOpenGeneric(typeof(TRequest), typeof(TResponse)).Factories
                          ?? Array.Empty<ObjectFactory>();

        var total = closedHandlers.Count + openMatches.Count;

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

        if (closedHandlers.Count == 1)
        {
            return closedHandlers[0];
        }

        // Exactly one open-generic match: build it via the cached factory, injecting its
        // dependencies from the current (scope-correct) provider. Open-generic request
        // handlers are created per request (transient) regardless of DefaultLifetime.
        return (IRequestHandler<TRequest, TResponse>)openMatches[0](serviceProvider, arguments: null);
    }
}
