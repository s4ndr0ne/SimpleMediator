using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Infrastructure;

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
        ThrowHelper.ThrowIfNull(serviceProvider);

        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>();
        if (scopeFactory is null)
        {
            return false;
        }

        if (ReferenceEquals(serviceProvider, scopeFactory))
        {
            return true;
        }

        // Both the root scope (which implements IServiceScopeFactory) and every caller-created
        // scope share the same internal runtime type (ServiceProviderEngineScope), whereas the
        // outer root ServiceProvider wrapper has a distinct runtime type. When serviceProvider
        // has the same runtime type as scopeFactory and is not reference-equal to it, it is
        // guaranteed to be a child scope without a second GetService<IServiceProvider>() lookup.
        if (serviceProvider.GetType() == scopeFactory.GetType())
        {
            return false;
        }

        var self = serviceProvider.GetService<IServiceProvider>();
        return !ReferenceEquals(self, serviceProvider) && ReferenceEquals(self, scopeFactory);
    }
}
