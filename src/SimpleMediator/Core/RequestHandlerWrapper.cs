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
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

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
