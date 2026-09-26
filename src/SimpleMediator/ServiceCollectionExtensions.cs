using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Configuration;
using SimpleMediator.Interfaces;
using SimpleMediator.Core;

namespace SimpleMediator;

/// <summary>
/// Extension methods for configuring SimpleMediator in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    internal const string ReflectionMessage =
        "SimpleMediator scans assemblies and resolves handlers via reflection; the referenced handler types may be removed by trimming.";
    internal const string DynamicCodeMessage =
        "SimpleMediator constructs generic wrapper and handler types at runtime (MakeGenericType), which is not supported by Native AOT.";

    /// <summary>
    /// Registers SimpleMediator services and handlers in the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">An action to configure <see cref="SimpleMediatorOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registers <see cref="IMediator"/>, <see cref="ISender"/> and <see cref="IPublisher"/>.
    /// Only Microsoft.Extensions.DependencyInjection is a supported container: scope detection,
    /// open-generic resolution, enumeration order and disposal are implemented and tested against it,
    /// and may behave differently when the collection is built by a third-party container.
    /// </remarks>
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
        // ISender and IPublisher forward to IMediator, so a replaced IMediator registration is
        // honored by all three and each resolution still uses the provider it was resolved from.
        services.TryAdd(new ServiceDescriptor(typeof(ISender), static provider => provider.GetRequiredService<IMediator>(), ServiceLifetime.Transient));
        services.TryAdd(new ServiceDescriptor(typeof(IPublisher), static provider => provider.GetRequiredService<IMediator>(), ServiceLifetime.Transient));
        services.TryAddScoped<OpenGenericScopedLifetimeStore>();
        services.TryAddSingleton<OpenGenericSingletonLifetimeStore>();

        var customOpenGenericRequestHandlers = MediatorAssemblyScanner.ScanAndRegister(services, options);
        var configuration = MergeConfiguration(services, options, customOpenGenericRequestHandlers);

        RegisterBehaviors(services, options);

        if (configuration.ValidationRequested)
        {
            services.ValidateSimpleMediator();
        }
        else
        {
            MediatorRegistrationValidator.ValidateRegistrationShape(services);
        }

        return services;
    }

    /// <summary>
    /// Validates structural open-generic/concrete registrations and configuration conflicts
    /// for closed request-handler registrations present in the service collection. It cannot
    /// validate request types without a closed handler registration. Returns the same
    /// collection for chaining.
    /// </summary>
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection ValidateSimpleMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        MediatorRegistrationValidator.Validate(services);

        var configuration = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(MediatorConfiguration))?
            .ImplementationInstance as MediatorConfiguration;
        configuration?.RequestValidation();

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
                AddBehavior(services, typeof(IPipelineBehavior<,>), behaviorType, options.DefaultLifetime);
                continue;
            }

            var closedInterfaces = behaviorType.GetInterfaces()
                .Where(implementedInterface =>
                    implementedInterface.IsGenericType &&
                    implementedInterface.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

            foreach (var closedInterface in closedInterfaces)
            {
                AddBehavior(services, closedInterface, behaviorType, options.DefaultLifetime);
            }
        }
    }

    private static void AddBehavior(IServiceCollection services, Type serviceType, Type behaviorType, ServiceLifetime lifetime)
    {
        // TryAddEnumerable keeps the FIRST registration for a (service type, implementation type)
        // pair and silently discards later ones, so the effective lifetime would otherwise depend on
        // module ordering. Detect the conflict before the descriptor is dropped.
        var existing = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == serviceType && descriptor.ImplementationType == behaviorType);

        if (existing is not null && existing.Lifetime != lifetime)
        {
            throw new InvalidOperationException(
                $"Pipeline behavior '{behaviorType.FullName}' is registered for '{serviceType.FullName}' with conflicting " +
                $"lifetimes ({existing.Lifetime} and {lifetime}). A behavior can only be registered once per " +
                "request/response pair. Align the lifetime across modules, or register a distinct behavior type per " +
                "configuration.");
        }

        services.TryAddEnumerable(new ServiceDescriptor(serviceType, behaviorType, lifetime));
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

    private static MediatorConfiguration MergeConfiguration(
        IServiceCollection services,
        SimpleMediatorOptions options,
        List<OpenGenericHandlerRegistration> customOpenGenericRequestHandlers)
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

            var mergedHandlers = existing.CustomOpenGenericRequestHandlers
                .Concat(customOpenGenericRequestHandlers)
                .GroupBy(registration => registration.ImplementationType)
                .Select(group => group.Last())
                .ToList();

            if (options.HasCustomRequireScopedMediator &&
                existing.RequireScopedMediatorIsExplicit &&
                existing.RequireScopedMediator != options.RequireScopedMediator)
            {
                throw new InvalidOperationException(
                    "SimpleMediatorOptions.RequireScopedMediator is set to conflicting values by different " +
                    "AddSimpleMediator calls. The root-mediator guard applies to the whole application, so one " +
                    "module cannot disable it for the others: set it in a single place, or to the same value everywhere.");
            }

            var requireScopedMediator = options.HasCustomRequireScopedMediator
                ? options.RequireScopedMediator
                : existing.RequireScopedMediator;

            var validationRequested = existing.ValidationRequested || options.ValidateOnBuild;
            var configuration = new MediatorConfiguration(
                strategy,
                mergedHandlers,
                capacity,
                validationRequested,
                requireScopedMediator,
                existing.RequireScopedMediatorIsExplicit || options.HasCustomRequireScopedMediator);

            services.Remove(existingDescriptor);
            services.AddSingleton(configuration);
            return configuration;
        }

        var initialConfiguration = new MediatorConfiguration(
            options.NotificationPublishStrategy,
            customOpenGenericRequestHandlers,
            options.OpenGenericResolutionCacheCapacity,
            options.ValidateOnBuild,
            options.RequireScopedMediator,
            options.HasCustomRequireScopedMediator);
        services.AddSingleton(initialConfiguration);
        return initialConfiguration;
    }
}
