namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines an optional ordering contract for pipeline behaviors.
/// </summary>
public interface IOrderedPipelineBehavior
{
    /// <summary>
    /// Execution priority. Lower values run first (outermost); behaviors with equal values
    /// run in registration order (FIFO): the first registered behavior is outermost and
    /// runs first. Defaults to <c>0</c>. The value is read once per request, so it may
    /// depend on scoped state, but it must be stable for the duration of a single request.
    /// </summary>
    int Order => 0;
}

/// <summary>
/// Pipeline behavior surrounding the execution of a request handler.
/// </summary>
/// <typeparam name="TRequest">The type of the request being processed.</typeparam>
/// <typeparam name="TResponse">The type of the response produced by the request handler.</typeparam>
public interface IPipelineBehavior<in TRequest, TResponse> : IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles the request within the pipeline.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="next">The delegate to invoke the next behavior or the handler itself.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation that yields the response.</returns>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
