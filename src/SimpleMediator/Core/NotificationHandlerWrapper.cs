using SimpleMediator.Interfaces;
using Microsoft.Extensions.DependencyInjection;
namespace SimpleMediator.Core;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();
        var typed = (TNotification)notification;

        // Default to Sequential when no configuration is registered (e.g. a Mediator
        // constructed directly without AddSimpleMediator). Sequential is the safe default:
        // handlers share the ambient DI scope, and a shared scoped service such as a
        // DbContext is not safe to use concurrently.
        var strategy = serviceProvider.GetService<MediatorConfiguration>()?.NotificationPublishStrategy
                       ?? NotificationPublishStrategy.Sequential;

        return strategy == NotificationPublishStrategy.Parallel
            ? PublishParallel(handlers, typed, cancellationToken)
            : PublishSequential(handlers, typed, cancellationToken);
    }

    private static async Task PublishSequential(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            await handler.Handle(notification, cancellationToken);
        }
    }

    private static async Task PublishParallel(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
    {
        // Invoke defensively: a handler that throws synchronously (e.g. a non-async
        // handler doing `=> throw`) must not abort the loop and prevent the other
        // handlers from running. Turn any synchronous throw into a faulted task so
        // every handler is started and every failure is aggregated below.
        var tasks = handlers.Select(h => InvokeSafely(h, notification, cancellationToken)).ToArray();
        if (tasks.Length == 0) return;

        var whenAll = Task.WhenAll(tasks);
        try
        {
            await whenAll;
        }
        catch
        {
            // await Task.WhenAll only rethrows the *first* faulted task's exception.
            // Re-throw the aggregate so callers can observe every handler failure.
            if (whenAll.Exception is not null)
            {
                throw whenAll.Exception;
            }

            throw;
        }
    }

    private static Task InvokeSafely(
        INotificationHandler<TNotification> handler,
        TNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            return handler.Handle(notification, cancellationToken) ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
