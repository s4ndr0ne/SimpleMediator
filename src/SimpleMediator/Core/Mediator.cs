using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

public class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    private static readonly ConcurrentDictionary<(Type Request, Type Response), Func<object>> _requestHandlerFactories = new();
    private static readonly ConcurrentDictionary<Type, Func<object>> _notificationHandlerFactories = new();

    public Mediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Send<Unit>(request, cancellationToken);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var requestType = request.GetType();

        // Key by both request and response type so cache entries remain correct even if callers
        // use explicit generic response types.
        var factory = _requestHandlerFactories.GetOrAdd((requestType, typeof(TResponse)), key =>
        {
            var wrapperType = typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(key.Request, key.Response);
            return Expression.Lambda<Func<object>>(Expression.New(wrapperType)).Compile();
        });

        var handler = (RequestHandlerWrapper<TResponse>)factory();

        return await handler.Handle(request, _serviceProvider, cancellationToken);
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        if (notification == null) throw new ArgumentNullException(nameof(notification));

        var notificationType = notification.GetType();

        var factory = _notificationHandlerFactories.GetOrAdd(notificationType, t =>
        {
            var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
            return Expression.Lambda<Func<object>>(Expression.New(wrapperType)).Compile();
        });

        var handler = (NotificationHandlerWrapper)factory();

        await handler.Handle(notification, _serviceProvider, cancellationToken);
    }
}
