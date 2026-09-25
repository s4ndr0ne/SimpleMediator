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
    /// Microsoft.Extensions.DependencyInjection gives the root provider's internal scope and every
    /// caller-created scope the same runtime type, so the type tells us nothing. The discriminator is
    /// the scope factory: <see cref="IServiceScopeFactory"/> always resolves to the container's root
    /// scope. Two shapes are therefore recognised as "root":
    /// <list type="bullet">
    /// <item>the root scope itself, which is what a service resolved from the root receives as its
    /// <see cref="IServiceProvider"/> (it <em>is</em> the scope factory);</item>
    /// <item>the public root <c>ServiceProvider</c> object, as passed to <c>new Mediator(root)</c>,
    /// which is not the scope factory but resolves <see cref="IServiceProvider"/> to it.</item>
    /// </list>
    /// A caller-created scope is neither. The check is advisory: on a container that wires these
    /// services differently it returns <c>false</c> rather than rejecting a legitimate mediator.
    /// Only Microsoft.Extensions.DependencyInjection is supported.
    /// </remarks>
    public static bool IsRootProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>();
        if (scopeFactory is null)
        {
            return false;
        }

        if (ReferenceEquals(serviceProvider, scopeFactory))
        {
            return true;
        }

        var self = serviceProvider.GetService<IServiceProvider>();
        return !ReferenceEquals(self, serviceProvider) && ReferenceEquals(self, scopeFactory);
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
