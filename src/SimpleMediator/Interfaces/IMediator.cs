using System.Diagnostics.CodeAnalysis;

namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a mediator to encapsulate request/response and publish/subscribe interactions.
/// </summary>
public interface IMediator
{
    /// <summary>
    /// Asynchronously sends a request to its single registered handler and returns the response.
    /// </summary>
    /// <typeparam name="TResponse">The type of the expected response.</typeparam>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation that yields the response.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates request wrappers and resolves handlers through reflection.")]
    [RequiresDynamicCode("SimpleMediator compiles request wrappers at runtime.")]
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously sends a void request (<see cref="IRequest"/>) to its single registered handler.
    /// </summary>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates request wrappers and resolves handlers through reflection.")]
    [RequiresDynamicCode("SimpleMediator compiles request wrappers at runtime.")]
    Task Send(IRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously publishes a notification to all registered notification handlers.
    /// </summary>
    /// <typeparam name="TNotification">The type of the notification being published.</typeparam>
    /// <param name="notification">The notification to publish.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates notification wrappers through reflection.")]
    [RequiresDynamicCode("SimpleMediator compiles notification wrappers at runtime.")]
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification;
}
