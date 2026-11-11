using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

/// <summary>
/// Extension methods for <see cref="ISender"/>.
/// </summary>
public static class SenderExtensions
{
    /// <summary>
    /// Asynchronously sends a request to its handler returning a <see cref="ValueTask{TResponse}"/>.
    /// Dispatches directly through the zero-allocation <see cref="ValueTask{TResponse}"/> fast-path when the mediator supports it.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="sender">The sender instance.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A value task representing the asynchronous operation that yields the response.</returns>
    public static ValueTask<TResponse> SendValueTask<TResponse>(
        this ISender sender,
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(sender);
        ThrowHelper.ThrowIfNull(request);

        if (sender is Mediator mediator)
        {
            return mediator.SendValueTask(request, cancellationToken);
        }

        return new ValueTask<TResponse>(sender.Send(request, cancellationToken));
    }
}
