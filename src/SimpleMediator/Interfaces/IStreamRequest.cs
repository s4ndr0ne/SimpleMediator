namespace SimpleMediator.Interfaces;

/// <summary>
/// Represents a streaming request that yields a sequence of <typeparamref name="TResponse"/> items.
/// </summary>
/// <typeparam name="TResponse">The type of items yielded by the stream.</typeparam>
public interface IStreamRequest<out TResponse>
{
}

/// <summary>
/// Defines a handler for a streaming request of type <typeparamref name="TRequest"/>.
/// </summary>
/// <typeparam name="TRequest">The type of streaming request being handled.</typeparam>
/// <typeparam name="TResponse">The type of items yielded by the handler.</typeparam>
public interface IStreamRequestHandler<in TRequest, out TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Handles a streaming request and returns an asynchronous sequence of responses.
    /// </summary>
    /// <param name="request">The streaming request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous sequence of <typeparamref name="TResponse"/> items.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the next step in a streaming request pipeline.
/// </summary>
/// <typeparam name="TResponse">The type of items yielded by the stream.</typeparam>
/// <returns>An asynchronous sequence of <typeparamref name="TResponse"/> items.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();

/// <summary>
/// Pipeline behavior surrounding the execution of a streaming request handler.
/// </summary>
/// <typeparam name="TRequest">The type of streaming request being processed.</typeparam>
/// <typeparam name="TResponse">The type of items yielded by the stream.</typeparam>
public interface IStreamPipelineBehavior<in TRequest, TResponse> : IOrderedPipelineBehavior
    where TRequest : IStreamRequest<TResponse>
{
    /// <summary>
    /// Handles the streaming request within the pipeline.
    /// </summary>
    /// <param name="request">The incoming streaming request.</param>
    /// <param name="next">The delegate to invoke the next behavior or the stream handler itself.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous sequence of <typeparamref name="TResponse"/> items.</returns>
    IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
