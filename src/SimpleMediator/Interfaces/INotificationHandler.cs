namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a handler for a notification of type <typeparamref name="TNotification"/>.
/// </summary>
/// <typeparam name="TNotification">The notification type being handled.</typeparam>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>
    /// Handles the notification asynchronously.
    /// </summary>
    /// <param name="notification">The notification instance.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous handle operation.</returns>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
