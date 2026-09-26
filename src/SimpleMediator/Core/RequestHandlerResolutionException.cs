namespace SimpleMediator.Core;

/// <summary>
/// Thrown when the mediator cannot select a handler for a request: no handler is registered, or
/// more than one is.
/// </summary>
/// <remarks>
/// This is a composition or wiring defect, not a failure of the request being processed, so it is
/// deliberately <em>not</em> offered to <c>IRequestExceptionHandler</c>. Letting an exception
/// handler observe it would hide a wiring bug behind whatever substitute response the handler
/// returns, and a handler that cannot even be constructed would replace a precise diagnostic with
/// an unrelated DI error. The same type is raised by the startup validators, so
/// <c>ValidateOnBuild</c> and the first failing request report identical wording.
/// </remarks>
public sealed class RequestHandlerResolutionException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="RequestHandlerResolutionException"/> class.</summary>
    public RequestHandlerResolutionException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RequestHandlerResolutionException"/> class.</summary>
    /// <param name="message">The message that describes the error.</param>
    public RequestHandlerResolutionException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RequestHandlerResolutionException"/> class.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of this exception.</param>
    public RequestHandlerResolutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
