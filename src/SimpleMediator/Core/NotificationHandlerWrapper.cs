using System.Runtime.ExceptionServices;
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
            await handler.Handle(notification, cancellationToken).ConfigureAwait(false);
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
            await whenAll.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // await Task.WhenAll only rethrows the *first* faulted task's exception.
            // Re-throw the aggregate so callers can observe every handler failure.
            var aggregate = whenAll.Exception;

            // Cancellation is not a handler failure: when the supplied token is
            // cancelled and every faulted handler threw OperationCanceledException,
            // surface the original OCE so callers see a normal cancellation rather
            // than an AggregateException wrapping it. (Matches the Send path.)
            if (aggregate is not null && IsCancellation(aggregate, cancellationToken))
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            if (aggregate is not null)
            {
                throw aggregate;
            }

            throw;
        }
    }

    private static bool IsCancellation(AggregateException aggregate, CancellationToken cancellationToken)
    {
        if (aggregate.InnerExceptions.Count == 0) return false;

        // The supplied token must be cancelled AND every inner exception must be
        // an OperationCanceledException. If any handler threw a *real* exception,
        // we keep the AggregateException path so the caller observes every failure.
        if (!cancellationToken.IsCancellationRequested) return false;

        foreach (var inner in aggregate.InnerExceptions)
        {
            if (inner is not OperationCanceledException) return false;
        }

        return true;
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
