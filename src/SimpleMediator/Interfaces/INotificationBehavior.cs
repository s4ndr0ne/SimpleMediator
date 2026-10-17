namespace SimpleMediator.Interfaces;

/// <summary>
/// Represents the next step in a notification pipeline.
/// </summary>
/// <param name="cancellationToken">The cancellation token for the operation.</param>
/// <returns>A task representing the asynchronous notification dispatch.</returns>
public delegate Task NotificationHandlerDelegate(CancellationToken cancellationToken = default);

/// <summary>
/// Pipeline behavior surrounding the publication of a notification.
/// </summary>
/// <typeparam name="TNotification">The type of notification being published.</typeparam>
public interface INotificationBehavior<in TNotification> : IOrderedPipelineBehavior
    where TNotification : INotification
{
    /// <summary>
    /// Handles the notification within the publish pipeline.
    /// </summary>
    /// <param name="notification">The notification being published.</param>
    /// <param name="next">The delegate to invoke the next behavior or the notification publisher.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken);
}

/// <summary>
/// Defines the strategy used to invoke the registered <see cref="INotificationHandler{TNotification}"/>
/// instances for a published notification. Register a custom implementation in DI to override the
/// built-in <see cref="NotificationPublishStrategy"/> behavior.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Publishes <paramref name="notification"/> to all <paramref name="handlers"/>.
    /// </summary>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    /// <param name="handlers">The resolved notification handlers.</param>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous publication.</returns>
    Task Publish<TNotification>(
        IEnumerable<INotificationHandler<TNotification>> handlers,
        TNotification notification,
        CancellationToken cancellationToken)
        where TNotification : INotification;
}
