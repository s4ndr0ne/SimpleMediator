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
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>();

        if (handler == null)
        {
            throw new InvalidOperationException($"No handler registered for {typeof(TRequest).Name}");
        }

        var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
        var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .Reverse();

        RequestHandlerDelegate<TResponse> next = async () =>
        {
            foreach (var pre in preHandlers)
            {
                await pre.Handle((TRequest)request, cancellationToken);
            }

            var response = await handler.Handle((TRequest)request, cancellationToken);

            foreach (var post in postHandlers)
            {
                await post.Handle((TRequest)request, response, cancellationToken);
            }

            return response;
        };

        foreach (var behavior in behaviors)
        {
            var currentNext = next;
            next = () => behavior.Handle((TRequest)request, currentNext, cancellationToken);
        }

        return next();
    }
}
