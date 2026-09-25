namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a pre-processor invoked before the main request handler runs.
/// </summary>
/// <typeparam name="TRequest">The type of the request being processed.</typeparam>
/// <typeparam name="TResponse">The type of response produced by the request handler.</typeparam>
public interface IPreRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Invoked before the request handler executes.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
