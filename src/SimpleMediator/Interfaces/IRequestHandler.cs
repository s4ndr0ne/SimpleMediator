namespace SimpleMediator.Interfaces;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken);

/// <summary>
/// Convenience base class for handlers of void requests (<see cref="IRequest"/>).
/// Implement <see cref="Handle"/> (the parameterless overload) and return a
/// <see cref="Task"/>; the mediator wraps the result in <see cref="Unit"/> for you.
/// </summary>
public abstract class RequestHandler<TRequest> : IRequestHandler<TRequest, Unit>
    where TRequest : IRequest
{
    public Task<Unit> Handle(TRequest request, CancellationToken cancellationToken)
    {
        var task = HandleCore(request, cancellationToken);
        return task.IsCompleted && task.Exception is null
            ? Unit.Task
            : ContinueWithUnit(task);
    }

    protected abstract Task HandleCore(TRequest request, CancellationToken cancellationToken);

    private static async Task<Unit> ContinueWithUnit(Task task)
    {
        await task.ConfigureAwait(false);
        return Unit.Value;
    }
}
