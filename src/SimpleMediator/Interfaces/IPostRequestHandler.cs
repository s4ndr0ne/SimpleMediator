namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a post-processor invoked after the main request handler has successfully produced a response.
/// </summary>
/// <typeparam name="TRequest">The type of the request being processed.</typeparam>
/// <typeparam name="TResponse">The type of response produced by the request handler.</typeparam>
public interface IPostRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Invoked after the request handler completes successfully.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="response">The response produced by the handler.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TRequest request, TResponse response, CancellationToken cancellationToken);
}
