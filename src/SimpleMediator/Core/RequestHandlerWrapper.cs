using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse> where TRequest : IRequest<TResponse>
{
    private readonly Func<IServiceProvider, IRequestHandler<TRequest, TResponse>> _handlerFactory;
    private readonly Func<IServiceProvider, IEnumerable<IPreRequestHandler<TRequest, TResponse>>> _preHandlersFactory;
    private readonly Func<IServiceProvider, IEnumerable<IPostRequestHandler<TRequest, TResponse>>> _postHandlersFactory;
    private readonly Func<IServiceProvider, IReadOnlyList<IPipelineBehavior<TRequest, TResponse>>> _behaviorsFactory;

    public RequestHandlerWrapperImpl()
    {
        _handlerFactory = sp => GetRequiredHandler(sp);
        _preHandlersFactory = sp => sp.GetServices<IPreRequestHandler<TRequest, TResponse>>();
        _postHandlersFactory = sp => sp.GetServices<IPostRequestHandler<TRequest, TResponse>>();
        _behaviorsFactory = sp => sp.GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .OrderBy(b => b.Order)
            .ToList();
    }

    [System.Diagnostics.DebuggerNonUserCode]
    private static IRequestHandler<TRequest, TResponse> GetRequiredHandler(IServiceProvider serviceProvider)
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>();
        if (handler == null)
            throw new InvalidOperationException($"No service for type '{typeof(IRequestHandler<TRequest, TResponse>).FullName}' has been registered.");
        return handler;
    }

    public override async Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = _handlerFactory(serviceProvider);
        var preHandlers = _preHandlersFactory(serviceProvider);
        var postHandlers = _postHandlersFactory(serviceProvider);
        var behaviors = _behaviorsFactory(serviceProvider);

        var typedRequest = (TRequest)request;

        RequestHandlerDelegate<TResponse> next = async ct =>
        {
            foreach (var pre in preHandlers)
            {
                await pre.Handle(typedRequest, ct);
            }

            var response = await handler.Handle(typedRequest, ct);

            foreach (var post in postHandlers)
            {
                await post.Handle(typedRequest, response, ct);
            }

            return response;
        };

        for (int i = behaviors.Count - 1; i >= 0; i--)
        {
            var currentNext = next;
            var behavior = behaviors[i];
            next = ct => behavior.Handle(typedRequest, currentNext, ct);
        }

        return await next(cancellationToken);
    }
}
