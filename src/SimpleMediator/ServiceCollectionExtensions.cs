using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

public static class ServiceCollectionExtensions
{
    private const string ReflectionMessage =
        "SimpleMediator scans assemblies and resolves handlers via reflection; the referenced handler types may be removed by trimming.";
    private const string DynamicCodeMessage =
        "SimpleMediator compiles expression trees and constructs generic handler types at runtime, which is not supported by Native AOT.";

    [RequiresUnreferencedCode(ReflectionMessage)]
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection AddSimpleMediator(this IServiceCollection services, Action<SimpleMediatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SimpleMediatorOptions();
        configure(options);
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
        // The mediator itself is stateless and only forwards the *ambient* IServiceProvider
        // to its wrappers. Registering it Transient (independent of DefaultLifetime) means a
        // mediator resolved inside a scope always uses that scope's provider, so scoped
        // handlers resolve correctly. Tying it to a Singleton DefaultLifetime would capture
        // the root provider and break scoped-handler resolution.
        services.TryAdd(new ServiceDescriptor(typeof(IMediator), typeof(Mediator), ServiceLifetime.Transient));

        // Open-generic request handlers (where the request type itself is generic) can't be
        // registered with Microsoft DI; collect them so the wrapper can close them on demand.
        var openGenericRequestHandlers = new List<Type>();

        foreach (var assembly in options.Assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            var candidateTypes = types
                .Where(t => !t.IsAbstract && !t.IsInterface);

            foreach (var type in candidateTypes)
            {
                if (type.IsGenericTypeDefinition)
                {
                    RegisterOpenGenericType(services, type, options.DefaultLifetime, openGenericRequestHandlers);
                    continue;
                }

                foreach (var @interface in type.GetInterfaces())
                {
                    if (!@interface.IsGenericType)
                    {
                        continue;
                    }

                    var genericTypeDefinition = @interface.GetGenericTypeDefinition();
                    if (genericTypeDefinition == typeof(IRequestHandler<,>) || genericTypeDefinition == typeof(INotificationHandler<>) ||
                        genericTypeDefinition == typeof(IPreRequestHandler<,>) || genericTypeDefinition == typeof(IPostRequestHandler<,>) ||
                        genericTypeDefinition == typeof(IRequestExceptionHandler<,>))
                    {
                        services.TryAddEnumerable(new ServiceDescriptor(@interface, type, options.DefaultLifetime));
                    }
                }
            }
        }

        // Runtime configuration consumed by the core wrappers (notification strategy,
        // open-generic request handlers to close on demand). Merge with any previous
        // AddSimpleMediator call so modular registrations (one per module) accumulate their
        // open-generic handlers; the last call wins for NotificationPublishStrategy.
        MergeConfiguration(services, options.NotificationPublishStrategy, openGenericRequestHandlers);

        // Execution order is determined by each behavior's Order property at request time
        // (see RequestHandlerWrapperImpl), so registration order here is irrelevant.
        foreach (var behaviorType in options.Behaviors)
        {
            if (behaviorType.IsGenericTypeDefinition)
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IPipelineBehavior<,>), behaviorType, options.DefaultLifetime));
            }
            else
            {
                // Closed/concrete behavior: register it against each closed IPipelineBehavior<,>
                // interface it implements so it can be resolved for those specific request types.
                var closedInterfaces = behaviorType.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

                foreach (var closedInterface in closedInterfaces)
                {
                    services.TryAddEnumerable(new ServiceDescriptor(closedInterface, behaviorType, options.DefaultLifetime));
                }
            }
        }

        if (options.ValidateOnBuild)
        {
            services.ValidateSimpleMediator();
        }

        return services;
    }

    /// <summary>
    /// Validates the SimpleMediator registrations and throws <see cref="InvalidOperationException"/>
    /// on the configuration errors that would otherwise only surface at request time:
    /// more than one handler for the same request, or a request matched by both a closed and an
    /// open-generic handler. Safe to call repeatedly; returns the same collection for chaining.
    /// </summary>
    [RequiresDynamicCode(DynamicCodeMessage)]
    public static IServiceCollection ValidateSimpleMediator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A closed IRequestHandler<TRequest,TResponse> must have exactly one registration —
        // any extra descriptor (whether by implementation type, factory, or instance) means
        // the mediator would see multiple handlers and throw at request time. Count raw
        // descriptors so factory/instance registrations are caught too. (TryAddEnumerable
        // already collapses identical type registrations, so a single scan yields one each.)
        var requestHandlerGroups = services
            .Where(d => d.ServiceType.IsGenericType
                        && !d.ServiceType.ContainsGenericParameters
                        && d.ServiceType.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .GroupBy(d => d.ServiceType)
            .ToList();

        foreach (var group in requestHandlerGroups)
        {
            if (group.Count() > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple request handlers registered for '{group.Key.FullName}'. A request can only have one handler.");
            }
        }

        // A closed handler and an open-generic handler must not both satisfy the same request.
        var configuration = services
            .LastOrDefault(d => d.ServiceType == typeof(MediatorConfiguration))?
            .ImplementationInstance as MediatorConfiguration;

        if (configuration is not null && configuration.OpenGenericRequestHandlers.Count > 0)
        {
            foreach (var group in requestHandlerGroups)
            {
                var typeArguments = group.Key.GetGenericArguments();
                foreach (var openHandler in configuration.OpenGenericRequestHandlers)
                {
                    if (OpenGenericMatcher.TryClose(openHandler, typeArguments[0], typeArguments[1], out _))
                    {
                        throw new InvalidOperationException(
                            $"Request '{typeArguments[0].FullName}' is matched by both a closed handler and the open-generic handler " +
                            $"'{openHandler.FullName}'. A request can only have one handler.");
                    }
                }
            }
        }

        ValidateOpenGenericBehaviors(services);

        return services;
    }

    // Verifies that every open-generic behavior registered against IPipelineBehavior<,> can
    // actually be closed by Microsoft DI (its implementation type parameters must line up
    // 1:1 with the service's). Mismatches would otherwise only surface at request time.
    private static void ValidateOpenGenericBehaviors(IServiceCollection services)
    {
        var openBehaviorDescriptors = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>) && d.ImplementationType is { IsGenericTypeDefinition: true })
            .Select(d => d.ImplementationType!)
            .ToList();

        foreach (var impl in openBehaviorDescriptors)
        {
            var pipelineInterface = impl.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

            if (pipelineInterface is null)
            {
                continue; // not a behavior after all; AddBehavior already guards this.
            }

            if (!CanRegisterWithNativeOpenGenericResolution(impl, pipelineInterface, typeof(IPipelineBehavior<,>)))
            {
                throw new InvalidOperationException(
                    $"Open-generic behavior '{impl.FullName}' cannot be closed by Microsoft DI: its type parameters " +
                    "do not line up 1:1 with IPipelineBehavior<TRequest, TResponse>. Rename them so the service and " +
                    "implementation share the same parameter list, or register a closed behavior instead.");
            }
        }
    }

    // Registers (or updates) the singleton MediatorConfiguration, accumulating open-generic
    // request handlers across repeated AddSimpleMediator calls. The latest call's
    // NotificationPublishStrategy wins.
    private static void MergeConfiguration(
        IServiceCollection services,
        NotificationPublishStrategy strategy,
        List<Type> openGenericRequestHandlers)
    {
        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(MediatorConfiguration));

        if (existingDescriptor?.ImplementationInstance is MediatorConfiguration existing)
        {
            var merged = existing.OpenGenericRequestHandlers
                .Concat(openGenericRequestHandlers)
                .Distinct()
                .ToList();

            services.Remove(existingDescriptor);
            services.AddSingleton(new MediatorConfiguration(strategy, merged));
        }
        else
        {
            services.AddSingleton(new MediatorConfiguration(strategy, openGenericRequestHandlers));
        }
    }

    // Routes an open-generic implementation to the right place:
    //  - request handlers (request type may be generic) -> collected for on-demand closing;
    //  - notification, pre/post, and exception catch-all handlers -> open-generic DI
    //    registration when impl and service type parameters line up 1:1.
    private static void RegisterOpenGenericType(
        IServiceCollection services,
        Type type,
        ServiceLifetime lifetime,
        List<Type> openGenericRequestHandlers)
    {
        var implementsRequestHandler = false;

        foreach (var @interface in type.GetInterfaces())
        {
            if (!@interface.IsGenericType)
            {
                continue;
            }

            var genericTypeDefinition = @interface.GetGenericTypeDefinition();

            if (genericTypeDefinition == typeof(IRequestHandler<,>))
            {
                implementsRequestHandler = true;
            }
            else if (CanRegisterWithNativeOpenGenericResolution(type, @interface, typeof(INotificationHandler<>)))
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(INotificationHandler<>), type, lifetime));
            }
            else if (CanRegisterWithNativeOpenGenericResolution(type, @interface, typeof(IPreRequestHandler<,>)))
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IPreRequestHandler<,>), type, lifetime));
            }
            else if (CanRegisterWithNativeOpenGenericResolution(type, @interface, typeof(IPostRequestHandler<,>)))
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IPostRequestHandler<,>), type, lifetime));
            }
            else if (CanRegisterWithNativeOpenGenericResolution(type, @interface, typeof(IRequestExceptionHandler<,>)))
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IRequestExceptionHandler<,>), type, lifetime));
            }
        }

        if (implementsRequestHandler && !openGenericRequestHandlers.Contains(type))
        {
            openGenericRequestHandlers.Add(type);
        }
    }

    private static bool CanRegisterWithNativeOpenGenericResolution(
        Type implementationType,
        Type implementedInterface,
        Type serviceTypeDefinition)
    {
        if (implementedInterface.GetGenericTypeDefinition() != serviceTypeDefinition)
        {
            return false;
        }

        var implementationArguments = implementationType.GetGenericArguments();
        var serviceArguments = implementedInterface.GetGenericArguments();

        return serviceArguments.Length == implementationArguments.Length
               && serviceArguments.SequenceEqual(implementationArguments);
    }
}
