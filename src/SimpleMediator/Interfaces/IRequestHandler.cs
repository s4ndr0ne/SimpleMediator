namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a handler for a request of type <typeparamref name="TRequest"/> returning <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response produced by the handler.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles the request asynchronously.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation that yields the response.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Represents a delegate for the next action in the request execution pipeline.
/// </summary>
/// <typeparam name="TResponse">The type of response.</typeparam>
/// <param name="cancellationToken">A cancellation token.</param>
/// <returns>A task representing the asynchronous operation that yields the response.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken);

/// <summary>
/// Convenience base class for handlers of void requests (<see cref="IRequest"/>).
/// Implement <see cref="HandleCore"/> and return a
/// <see cref="Task"/>; the mediator wraps the result in <see cref="Unit"/> for you.
/// </summary>
public abstract class RequestHandler<TRequest> : IRequestHandler<TRequest, Unit>
    where TRequest : IRequest
{
    /// <summary>
    /// Handles the request by calling <see cref="HandleCore"/> and returning <see cref="Unit"/>.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the operation yielding <see cref="Unit"/>.</returns>
    public Task<Unit> Handle(TRequest request, CancellationToken cancellationToken)
    {
        var task = HandleCore(request, cancellationToken);
        if (task is null)
        {
            throw new InvalidOperationException(
                $"'{GetType().FullName}.HandleCore' returned a null Task. Every mediator handler method must return a non-null Task.");
        }

        // A cancelled Task is completed and has no Exception, but it must still
        // propagate its OperationCanceledException to the caller. Status comparison is
        // Task.IsCompletedSuccessfully spelled for netstandard2.0.
        return task.Status == TaskStatus.RanToCompletion
            ? Unit.Task
            : ContinueWithUnit(task);
    }

    /// <summary>
    /// Core handling logic to be implemented by derived classes.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected abstract Task HandleCore(TRequest request, CancellationToken cancellationToken);

    private static async Task<Unit> ContinueWithUnit(Task task)
    {
        await task.ConfigureAwait(false);
        return Unit.Value;
    }
}
