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
        var handlerLease = RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider);

        try
        {
            var handler = handlerLease.Handler;
            var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
            var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
            var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

            return await RequestHandlerPipeline.Execute(
                (TRequest)request,
                handler,
                preHandlers,
                postHandlers,
                behaviors,
                serviceProvider,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Closed handlers are owned and tracked by Microsoft DI. Open-generic
            // handlers are activated manually and are owned by this request.
            await handlerLease.DisposeAsync().ConfigureAwait(false);
        }
    }
}


