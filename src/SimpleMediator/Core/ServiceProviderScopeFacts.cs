using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator.Core;

/// <summary>
/// Host-specific helpers for reasoning about the DI scope that owns a provider.
/// </summary>
internal static class ServiceProviderScopeFacts
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="serviceProvider"/> is the application's root
    /// provider rather than a scope.
    /// </summary>
    /// <remarks>
    /// Microsoft.Extensions.DependencyInjection implements <see cref="IServiceScope"/> on every
    /// scope it creates, but not on the root <c>ServiceProvider</c>. The check therefore uses a
    /// documented public interface instead of internal type names. The root provider is still a
    /// legal place to <em>create</em> scopes; it is only illegal to resolve mediator services
    /// from it directly (see <see cref="Mediator"/>).
    /// </remarks>
    public static bool IsRootProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // Microsoft.Extensions.DependencyInjection hands every scope — including the one the root
        // provider uses internally — the same runtime type, so the type itself tells us nothing.
        // What does distinguish them is the scope factory: `IServiceScopeFactory` is always the
        // container's ROOT scope, and a service resolved from that root scope is handed that very
        // object. A service resolved from a caller-created scope receives a different instance.
        //
        // The comparison is deliberately advisory. If a container wires the scope factory
        // differently the guard simply does not fire rather than rejecting a legitimate mediator.
        return ReferenceEquals(serviceProvider, serviceProvider.GetService<IServiceScopeFactory>());
    }
}

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
