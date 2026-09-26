using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Configuration;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;

namespace SimpleMediator.Generated;

/// <summary>
/// Registration entry point called by the <c>AddSimpleMediatorGenerated</c> extension method that the
/// SimpleMediator source generator emits into the consuming project.
/// </summary>
/// <remarks>
/// Infrastructure for generated code; not intended to be called directly. Unlike the reflection-based
/// <see cref="ServiceCollectionExtensions.AddSimpleMediator(IServiceCollection, Action{SimpleMediatorOptions})"/>,
/// this path performs no assembly scanning and closes no generic type at runtime, so it is safe for
/// trimming and Native AOT.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedMediatorRegistration
{
    /// <summary>
    /// Registers SimpleMediator from a compile-time generated registration table.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional options callback. Only assemblies scanned by the generator may be passed to <see cref="SimpleMediatorOptions.RegisterAssembly(System.Reflection.Assembly)"/>.</param>
    /// <param name="populate">Generated callback that fills the registration table.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection Register(
        IServiceCollection services,
        Action<SimpleMediatorOptions>? configure,
        Action<GeneratedMediatorRegistry> populate)
    {
        ThrowHelper.ThrowIfNull(services);
        ThrowHelper.ThrowIfNull(populate);

        var options = new SimpleMediatorOptions();
        configure?.Invoke(options);
        ServiceCollectionExtensions.ValidateOptions(options);

        var registry = new GeneratedMediatorRegistry();
        populate(registry);

        foreach (var assembly in options.Assemblies)
        {
            if (!registry.ScannedAssemblies.Contains(assembly))
            {
                throw new InvalidOperationException(
                    $"Assembly '{assembly.FullName}' was passed to RegisterAssembly but was not scanned by the SimpleMediator " +
                    "source generator. The generator scans the current project and every assembly passed to RegisterAssembly " +
                    "as 'typeof(SomeType).Assembly' (or Assembly.GetExecutingAssembly()); an assembly computed at runtime " +
                    "cannot be scanned at compile time. Use the reflection-based AddSimpleMediator for runtime-selected assemblies.");
            }
        }

        ServiceCollectionExtensions.AddMediatorServices(services);

        // Same selection and order as assembly scanning: assemblies in RegisterAssembly order, types in
        // ordinal full-name order within an assembly (the generator emits them sorted), filtered by the
        // assembly's predicate, registered with the default lifetime.
        foreach (var assembly in options.Assemblies)
        {
            options.AssemblyFilters.TryGetValue(assembly, out var filter);
            foreach (var (declaredType, descriptor) in registry.Handlers)
            {
                if (declaredType.Assembly == assembly && (filter is null || filter(declaredType)))
                {
                    services.TryAddEnumerable(descriptor(options.DefaultLifetime));
                }
            }
        }

        var configuration = ServiceCollectionExtensions.MergeConfiguration(
            services,
            options,
            new List<OpenGenericHandlerRegistration>(),
            registry);

        // Behaviors stay opt-in and ordered exactly as with the reflection-based registration: the
        // generator pre-closes every behavior it can see, and only the ones passed to AddBehavior are
        // registered, in AddBehavior order.
        foreach (var behaviorType in options.Behaviors)
        {
            if (!registry.Behaviors.TryGetValue(behaviorType, out var behaviors))
            {
                throw new InvalidOperationException(
                    $"Pipeline behavior '{behaviorType.FullName}' is not known to the SimpleMediator source generator. " +
                    "Declare it in the project that calls AddSimpleMediatorGenerated, or pass it to AddBehavior as a " +
                    "typeof(...) literal so the generator can close it for every request at compile time.");
            }

            foreach (var behavior in behaviors)
            {
                ServiceCollectionExtensions.AddBehaviorDescriptor(services, behavior(options.DefaultLifetime));
            }
        }

        if (configuration.ValidationRequested)
        {
            ValidateSingleRequestHandler(services);
        }

        return services;
    }

    // The trim-safe subset of ValidateSimpleMediator: the generator already rejects duplicate handlers
    // it can see, this also covers handlers registered by hand next to the generated ones.
    private static void ValidateSingleRequestHandler(IServiceCollection services)
    {
        var duplicate = services
            .Where(descriptor => OpenGenericRegistrationRules.IsClosedRequestHandler(descriptor.ServiceType))
            .GroupBy(descriptor => descriptor.ServiceType)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new RequestHandlerResolutionException(
                $"Multiple request handlers registered for '{duplicate.Key.FullName}'. A request can only have one handler.");
        }
    }
}
