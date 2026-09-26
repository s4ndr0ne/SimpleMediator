namespace SimpleMediator.Interfaces;

/// <summary>
/// Carries the outcome of exception handling for a request. A handler that can recover
/// from the exception calls <see cref="SetHandled"/> with a substitute response; the
/// mediator then returns that response instead of rethrowing.
/// </summary>
public sealed class RequestExceptionHandlerState<TResponse>
{
    /// <summary>True once a handler has supplied a substitute response.</summary>
    public bool Handled { get; private set; }

    /// <summary>The substitute response, valid only when <see cref="Handled"/> is true.</summary>
    public TResponse? Response { get; private set; }

    /// <summary>Marks the exception as handled and supplies the response to return to the caller.</summary>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}

/// <summary>
/// Handles exceptions thrown while processing a request (by the handler, its pre/post
/// handlers, or any pipeline behavior). Registered handlers run in ascending Order;
/// the first one to call <see cref="RequestExceptionHandlerState{TResponse}.SetHandled"/>
/// short-circuits the rest and the exception is swallowed. If none handles it, the
/// original exception is rethrown with its stack trace intact.
/// </summary>
/// <remarks>
/// A catch-all handler is an open generic
/// <c>class MyExHandler&lt;TRequest, TResponse&gt; : IRequestExceptionHandler&lt;TRequest, TResponse&gt;</c>;
/// it is picked up automatically by assembly scanning.
/// </remarks>
public interface IRequestExceptionHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Execution order: lower runs first. Defaults to <c>0</c>, like
    /// <c>IPipelineBehavior&lt;TRequest, TResponse&gt;.Order</c>. Handlers with equal values
    /// keep their DI resolution order. The value is read once per resolution, so it may depend on
    /// scoped state, but it must be stable for the duration of a single resolution. When the
    /// relative order of scanned handlers must be fixed, give them distinct values: assembly-scan
    /// discovery order is stable by type name but is not a contract.
    /// </summary>
    /// <remarks>
    /// On runtimes without default-interface support (netstandard2.0) implementers must
    /// declare the property explicitly; returning <c>0</c> reproduces the default.
    /// </remarks>
#if NETSTANDARD2_0
    int Order { get; }
#else
    int Order => 0;
#endif

    /// <summary>
    /// Handles the exception thrown during request processing.
    /// </summary>
    /// <param name="request">The incoming request that faulted.</param>
    /// <param name="exception">The exception that was thrown.</param>
    /// <param name="state">The state object used to supply a replacement response and mark the exception handled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Handle(TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
}
