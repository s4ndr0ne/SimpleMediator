using System.Diagnostics.CodeAnalysis;

namespace SimpleMediator.Interfaces;

/// <summary>
/// Publishes notifications to every registered handler (the publish/subscribe half of the mediator).
/// </summary>
/// <remarks>
/// Depend on <see cref="IPublisher"/> instead of <see cref="IMediator"/> when a component only raises
/// notifications, for example a domain-event dispatcher. Resolve it inside the request or operation
/// scope, exactly like <see cref="IMediator"/>.
/// </remarks>
public interface IPublisher
{
    /// <summary>
    /// Asynchronously publishes a notification to all registered notification handlers.
    /// </summary>
    /// <typeparam name="TNotification">The type of the notification being published.</typeparam>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates notification wrappers through reflection.")]
    [RequiresDynamicCode("SimpleMediator constructs generic notification wrapper types at runtime.")]
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification;
}
