using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

public class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    // Wrapper caches belong to the DI configuration. This avoids a process-wide static cache
    // retaining types from collectible plugin AssemblyLoadContexts.
    private readonly BoundedFactoryCache<(Type Request, Type Response), object> _requestHandlerWrappers;
    private readonly BoundedFactoryCache<Type, object> _notificationHandlerWrappers;

    public Mediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        var configuration = serviceProvider.GetService<MediatorConfiguration>();
        _requestHandlerWrappers = configuration?.RequestHandlerWrappers
            ?? new BoundedFactoryCache<(Type Request, Type Response), object>(1024);
        _notificationHandlerWrappers = configuration?.NotificationHandlerWrappers
            ?? new BoundedFactoryCache<Type, object>(1024);
    }

    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Send<Unit>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        // Key by both request and response type so cache entries remain correct even if callers
        // use explicit generic response types.
        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlerWrappers.GetOrAdd((requestType, typeof(TResponse)), key =>
        {
            var wrapperType = typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(key.Request, key.Response);
            return Expression.Lambda<Func<object>>(Expression.New(wrapperType)).Compile()();
        });

        return await handler.Handle(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        var notificationType = notification.GetType();

        var handler = (NotificationHandlerWrapper)_notificationHandlerWrappers.GetOrAdd(notificationType, t =>
        {
            var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
            return Expression.Lambda<Func<object>>(Expression.New(wrapperType)).Compile()();
        });

        await handler.Handle(notification, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }
}
