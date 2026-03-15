using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

public class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    private static readonly ConcurrentDictionary<Type, object> _requestHandlers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> _notificationHandlers = new();

    public Mediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        await Send<Unit>(request, cancellationToken);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var requestType = request.GetType();

        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlers.GetOrAdd(requestType,
            t => Activator.CreateInstance(typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(t, typeof(TResponse)))!);

        // create a scope for resolving handlers so scoped services work correctly
        using var scope = _serviceProvider.CreateScope();
        return await handler.Handle(request, scope.ServiceProvider, cancellationToken);
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        if (notification == null) throw new ArgumentNullException(nameof(notification));

        var notificationType = notification.GetType();

        var handler = _notificationHandlers.GetOrAdd(notificationType,
            t => (NotificationHandlerWrapper)Activator.CreateInstance(typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t))!);

        using var scope = _serviceProvider.CreateScope();
        await handler.Handle(notification, scope.ServiceProvider, cancellationToken);
    }  
    
}
