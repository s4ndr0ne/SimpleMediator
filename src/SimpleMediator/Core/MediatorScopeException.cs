namespace SimpleMediator.Core;

/// <summary>
/// Thrown when a mediator is resolved from the root service provider while the
/// scope guard is enabled. Root-owned mediators silently promote every scoped service
/// (a <c>DbContext</c>, a unit of work) to a single process-wide instance, so SimpleMediator
/// rejects the pattern instead of documenting it.
/// </summary>
public sealed class MediatorScopeException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="MediatorScopeException"/> class.</summary>
    public MediatorScopeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MediatorScopeException"/> class.</summary>
    /// <param name="message">The message that describes the error.</param>
    public MediatorScopeException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MediatorScopeException"/> class.</summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of this exception.</param>
    public MediatorScopeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
