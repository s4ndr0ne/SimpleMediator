using System.Runtime.ExceptionServices;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
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
    private readonly MediatorConfiguration? _configuration;
    private readonly BehaviorOrderCache _behaviorOrder = new();
    private volatile int _behaviorState;
    private volatile int _customPublisherState;

    public NotificationHandlerWrapperImpl()
    {
    }

    public NotificationHandlerWrapperImpl(MediatorConfiguration? configuration)
    {
        _configuration = configuration;
    }

    public override Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typed = (TNotification)notification;
        var configuration = _configuration ?? serviceProvider.GetService<MediatorConfiguration>();

#if !NETSTANDARD2_0
        if (configuration is { EnableTelemetry: true } && SimpleMediatorDiagnostics.HasTelemetryListeners())
        {
            return SimpleMediatorDiagnostics.TraceNotificationAsync<TNotification>(
                HandleCore(typed, serviceProvider, configuration, cancellationToken));
        }
#endif

        return HandleCore(typed, serviceProvider, configuration, cancellationToken);
    }

    private Task HandleCore(
        TNotification typed,
        IServiceProvider serviceProvider,
        MediatorConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();
        INotificationPublisher? customPublisher = null;
        if (_customPublisherState != 1)
        {
            customPublisher = serviceProvider.GetService<INotificationPublisher>();
            if (_customPublisherState == 0)
            {
                _customPublisherState = customPublisher is null ? 1 : 2;
            }
        }

        var strategy = configuration?.NotificationPublishStrategy ?? NotificationPublishStrategy.Sequential;

        if (_behaviorState != 1)
        {
            var behaviors = serviceProvider.GetServices<INotificationBehavior<TNotification>>();
            var behaviorList = behaviors as IReadOnlyList<INotificationBehavior<TNotification>>
                               ?? behaviors.ToArray();
            if (_behaviorState == 0)
            {
                _behaviorState = behaviorList.Count == 0 ? 1 : 2;
            }

            if (behaviorList.Count > 0)
            {
                return ExecuteWithBehaviors(
                    typed,
                    handlers,
                    customPublisher,
                    strategy,
                    behaviorList,
                    _behaviorOrder.GetNotificationOrder(behaviorList),
                    cancellationToken);
            }
        }

        return PublishCore(handlers, typed, customPublisher, strategy, cancellationToken);
    }

    private static Task ExecuteWithBehaviors(
        TNotification notification,
        IEnumerable<INotificationHandler<TNotification>> handlers,
        INotificationPublisher? customPublisher,
        NotificationPublishStrategy strategy,
        IReadOnlyList<INotificationBehavior<TNotification>> behaviors,
        int[] behaviorOrder,
        CancellationToken cancellationToken)
    {
        NotificationHandlerDelegate publishDelegate = ct =>
            PublishCore(handlers, notification, customPublisher, strategy, ct);

        var aggregate = publishDelegate;
        for (var index = 0; index < behaviorOrder.Length; index++)
        {
            var behavior = behaviors[behaviorOrder[index]];
            var next = aggregate;
            aggregate = ct => behavior.Handle(notification, next, ct)
                              ?? throw RequestHandlerPipeline.NullTaskException(behavior);
        }

        return aggregate(cancellationToken);
    }

    private static Task PublishCore(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        INotificationPublisher? customPublisher,
        NotificationPublishStrategy strategy,
        CancellationToken cancellationToken)
    {
        if (customPublisher is not null)
        {
            return customPublisher.Publish(handlers, notification, cancellationToken)
                   ?? throw RequestHandlerPipeline.NullTaskException(customPublisher);
        }

        return strategy == NotificationPublishStrategy.Parallel
            ? PublishParallel(handlers, notification, cancellationToken)
            : PublishSequential(handlers, notification, cancellationToken);
    }

    private static async Task PublishSequential(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
    {
        // Microsoft DI hands back an array: index it directly so the loop does not go through
        // the IEnumerable interface, whose GetEnumerator boxes the array's struct enumerator on
        // every publish. Materialize any other shape once as the fallback.
        if (handlers is not INotificationHandler<TNotification>[] handlerArray)
        {
            handlerArray = handlers.ToArray();
        }

        for (var index = 0; index < handlerArray.Length; index++)
        {
            await InvokeSafely(handlerArray[index], notification, cancellationToken).ConfigureAwait(false);
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
        var handlerArray = handlers as INotificationHandler<TNotification>[] ?? handlers.ToArray();
        if (handlerArray.Length == 0) return;

        Task[] tasks;
        if (handlerArray.Length == 1)
        {
            var singleTask = InvokeSafely(handlerArray[0], notification, cancellationToken);
            if (singleTask.Status == TaskStatus.RanToCompletion) return;

            // Avoid Task.WhenAll allocation on the hot success path. Faulted single-handler
            // requests are normalized below to the original exception.
            tasks = new[] { singleTask };
        }
        else
        {
            tasks = new Task[handlerArray.Length];
            for (var index = 0; index < handlerArray.Length; index++)
            {
                tasks[index] = InvokeSafely(handlerArray[index], notification, cancellationToken);
            }
        }

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
                // Preserve the original exception for a single failed handler, matching
                // sequential dispatch. Aggregate only when more than one handler failed.
                if (aggregate.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
                }

                throw aggregate;
            }

            throw;
        }
    }

    private static bool IsCancellation(AggregateException aggregate, CancellationToken cancellationToken)
    {
        if (aggregate.InnerExceptions.Count == 0) return false;

        // If every faulted handler threw an OperationCanceledException,
        // treat it as a cancellation so callers observe OperationCanceledException.
        foreach (var inner in aggregate.InnerExceptions)
        {
            if (inner is not OperationCanceledException) return false;
        }

        return cancellationToken.IsCancellationRequested;
    }

    private static Task InvokeSafely(
        INotificationHandler<TNotification> handler,
        TNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            return handler.Handle(notification, cancellationToken)
                   ?? throw new InvalidOperationException(
                       $"Notification handler '{handler.GetType().FullName}' returned a null Task.");
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
