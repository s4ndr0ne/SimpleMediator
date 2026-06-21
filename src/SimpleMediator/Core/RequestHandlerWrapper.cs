using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse> where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<IRequestHandler<TRequest, TResponse>>();
        using var enumerator = handlers.GetEnumerator();

        if (!enumerator.MoveNext())
        {
            throw new InvalidOperationException(
                $"No request handler registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'.");
        }

        var handler = enumerator.Current;

        if (enumerator.MoveNext())
        {
            throw new InvalidOperationException(
                $"Multiple request handlers registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'. " +
                "A request can only have one handler.");
        }

        var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
        var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

        // The delegate chain is built on each request, but we avoid the LINQ Overhead where possible.
        // To truly cache the delegate chain, we would need to handle the lifetime of resolved services carefully.
        // For now, we optimize by avoiding repeated OrderBy/Reverse if possible, 
        // though IPipelineBehavior depends on IOrderedPipelineBehavior.

        RequestHandlerDelegate<TResponse> handlerDelegate = async (ct) =>
        {
            foreach (var pre in preHandlers)
            {
                await pre.Handle((TRequest)request, ct);
            }

            var response = await handler.Handle((TRequest)request, ct);

            foreach (var post in postHandlers)
            {
                await post.Handle((TRequest)request, response, ct);
            }

            return response;
        };

        // Build the behavior chain
        var aggregate = behaviors
            .OrderByDescending(b => b.Order)
            .Aggregate(handlerDelegate, (next, behavior) => ct => behavior.Handle((TRequest)request, next, ct));

        return aggregate(cancellationToken);
    }
}
