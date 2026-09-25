using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

/// <summary>
/// Extension methods for configuring SimpleMediator in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    internal const string ReflectionMessage =
        "SimpleMediator scans assemblies and resolves handlers via reflection; the referenced handler types may be removed by trimming.";
    internal const string DynamicCodeMessage =
        "SimpleMediator compiles expression trees and constructs generic handler types at runtime, which is not supported by Native AOT.";

    /// <summary>
    /// Registers SimpleMediator services and handlers in the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">An action to configure <see cref="SimpleMediatorOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection AddSimpleMediator(this IServiceCollection services, Action<SimpleMediatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SimpleMediatorOptions();
        configure(options);
        ValidateOptions(options);
        return AddSimpleMediatorCore(services, options);
    }

    /// <summary>
    /// Convenience overload: registers SimpleMediator with default options (no extra
    /// assemblies, no behaviors). Equivalent to <c>AddSimpleMediator(_ => {})</c>. Pair
    /// it with explicit handler registrations, or follow it with another
    /// <c>AddSimpleMediator</c> call that calls <c>RegisterAssembly</c>.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection AddSimpleMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddSimpleMediatorCore(services, new SimpleMediatorOptions());
    }

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    private static IServiceCollection AddSimpleMediatorCore(IServiceCollection services, SimpleMediatorOptions options)
    {
        // The mediator itself is stateless and forwards the ambient provider to its wrappers.
        // Transient registration ensures a mediator resolved inside a scope uses that scope's
        // provider, even when handler lifetimes are configured as singleton.
        services.TryAdd(new ServiceDescriptor(typeof(IMediator), typeof(Mediator), ServiceLifetime.Transient));

        var openGenericRequestHandlers = MediatorAssemblyScanner.ScanAndRegister(services, options);
        MergeConfiguration(services, options, openGenericRequestHandlers);

        RegisterBehaviors(services, options);

        if (options.ValidateOnBuild)
        {
            services.ValidateSimpleMediator();
        }

        return services;
    }

    /// <summary>
    /// Validates configuration conflicts for closed request-handler registrations present
    /// in the service collection. It cannot validate request types without a closed handler
    /// registration. Returns the same collection for chaining.
    /// </summary>
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection ValidateSimpleMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        MediatorRegistrationValidator.Validate(services);
        return services;
    }

    private static void RegisterBehaviors(IServiceCollection services, SimpleMediatorOptions options)
    {
        // Order determines the primary execution sequence. Behaviors with equal Order run
        // in registration order (FIFO): the first registered behavior is outermost;
        // values are evaluated by the wrapper for each request.
        foreach (var behaviorType in options.Behaviors)
        {
            if (behaviorType.IsGenericTypeDefinition)
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IPipelineBehavior<,>), behaviorType, options.DefaultLifetime));
                continue;
            }

            var closedInterfaces = behaviorType.GetInterfaces()
                .Where(implementedInterface =>
                    implementedInterface.IsGenericType &&
                    implementedInterface.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

            foreach (var closedInterface in closedInterfaces)
            {
                services.TryAddEnumerable(new ServiceDescriptor(closedInterface, behaviorType, options.DefaultLifetime));
            }
        }
    }

    private static void ValidateOptions(SimpleMediatorOptions options)
    {
        if (!Enum.IsDefined(options.DefaultLifetime))
        {
            throw new ArgumentException("DefaultLifetime must be a defined ServiceLifetime value.");
        }

        if (!Enum.IsDefined(options.NotificationPublishStrategy))
        {
            throw new ArgumentException("NotificationPublishStrategy must be a defined NotificationPublishStrategy value.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.OpenGenericResolutionCacheCapacity);
    }

    private static void MergeConfiguration(
        IServiceCollection services,
        SimpleMediatorOptions options,
        List<Type> openGenericRequestHandlers)
    {
        var existingDescriptor = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(MediatorConfiguration));

        if (existingDescriptor?.ImplementationInstance is MediatorConfiguration existing)
        {
            var strategy = options.HasCustomPublishStrategy
                ? options.NotificationPublishStrategy
                : existing.NotificationPublishStrategy;

            var capacity = options.HasCustomCacheCapacity
                ? options.OpenGenericResolutionCacheCapacity
                : existing.ResolutionCacheCapacity;

            var mergedHandlers = existing.OpenGenericRequestHandlers
                .Concat(openGenericRequestHandlers)
                .Distinct()
                .ToList();

            services.Remove(existingDescriptor);
            services.AddSingleton(new MediatorConfiguration(strategy, mergedHandlers, capacity));
            return;
        }

        services.AddSingleton(new MediatorConfiguration(
            options.NotificationPublishStrategy,
            openGenericRequestHandlers,
            options.OpenGenericResolutionCacheCapacity));
    }
}
