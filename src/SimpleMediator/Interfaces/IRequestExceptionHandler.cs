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
/// handlers, or any pipeline behavior). Registered handlers run in registration order;
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
    /// Execution order: lower runs first. Defaults to <c>0</c>, so handlers that don't
    /// override it run in registration order. Set an explicit value to make ordering
    /// deterministic — assembly-scan discovery order is not guaranteed, so handlers that
    /// share the same <see cref="Order"/> run in an unspecified order relative to each other.
    /// </summary>
    int Order => 0;

    Task Handle(TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken);
}
