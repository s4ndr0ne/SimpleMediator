namespace SimpleMediator.Interfaces;

public interface IOrderedPipelineBehavior
{
    /// <summary>
    /// Execution priority. Lower values run first; behaviors with equal values run in
    /// their DI resolution order. The value is read from the current instance per request.
    /// </summary>
    int Order { get; }
}

public interface IPipelineBehavior<in TRequest, TResponse> : IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
