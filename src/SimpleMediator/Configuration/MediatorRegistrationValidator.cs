using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;
using SimpleMediator;

namespace SimpleMediator.Configuration;

/// <summary>
/// Validates request-handler, open-generic, behavior, and concrete implementation
/// registrations before the service provider is built.
/// </summary>
internal static class MediatorRegistrationValidator
{
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    internal static void Validate(IServiceCollection services)
    {
        var requestHandlerGroups = services
            .Where(descriptor => OpenGenericRegistrationRules.IsClosedRequestHandler(descriptor.ServiceType))
            .GroupBy(descriptor => descriptor.ServiceType)
            .ToList();

        foreach (var group in requestHandlerGroups)
        {
            if (group.Count() > 1)
            {
                throw new Core.RequestHandlerResolutionException(
                    $"Multiple request handlers registered for '{group.Key.FullName}'. A request can only have one handler.");
            }
        }

        ValidateRegistrationShape(services);
        ValidateScannedOpenGenericHandlerConflicts(services, requestHandlerGroups);
        ValidateNativeOpenGenericHandlers(services, requestHandlerGroups);
    }

    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    internal static void ValidateRegistrationShape(IServiceCollection services)
    {
        ValidateScannedOpenGenericHandlers(services);
        ValidateOpenGenericRegistrations(services);
        ValidateConcreteImplementations(services);
        // Structural, and cheap enough to always run: a singleton custom-mapped handler holding a
        // scoped dependency is a guaranteed production failure, so it must not wait for opt-in
        // validation the way duplicate-handler conflicts do.
        ValidateSingletonCustomOpenGenericHandlers(services);
    }

    /// <summary>
    /// A custom-mapped open-generic request handler is activated by SimpleMediator rather than by
    /// the container, so a <see cref="ServiceLifetime.Singleton"/> registration cannot be honoured
    /// the way DI honours it for closed handlers: the handler is built once and reused forever, and
    /// any scoped or transient constructor dependency it captures stays alive after the scope that
    /// produced it has been disposed. Reject that combination at startup with an actionable message
    /// instead of shipping a handler that holds a disposed <c>DbContext</c>.
    /// </summary>
    private static void ValidateSingletonCustomOpenGenericHandlers(IServiceCollection services)
    {
        var configuration = GetConfiguration(services);
        if (configuration is null)
        {
            return;
        }

        foreach (var registration in configuration.CustomOpenGenericRequestHandlers
                     .DistinctBy(item => item.ImplementationType))
        {
            if (registration.Lifetime != ServiceLifetime.Singleton)
            {
                continue;
            }

            var handlerType = registration.ImplementationType;
            foreach (var constructor in CandidateConstructors(handlerType))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    ValidateSingletonDependency(services, handlerType, parameter.ParameterType);
                }
            }
        }
    }

    /// <summary>
    /// The constructors Microsoft DI may use to build <paramref name="implementationType"/>: the one
    /// marked with <see cref="ActivatorUtilitiesConstructorAttribute"/> when present, otherwise every
    /// public constructor. Checking all of them is deliberately conservative — a lifetime error in any
    /// constructor the container could pick is still a latent production failure.
    /// </summary>
    private static ConstructorInfo[] CandidateConstructors(Type implementationType)
    {
        var constructors = implementationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        var marked = constructors.Where(constructor => constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false)).ToArray();
        return marked.Length > 0 ? marked : constructors;
    }

    private static void ValidateSingletonDependency(IServiceCollection services, Type handlerType, Type dependencyType)
    {
        // A dependency on IEnumerable<T> receives every registration of T, so the shortest-lived of
        // them decides whether the singleton would capture a scoped or transient instance.
        var isEnumerable = dependencyType.IsGenericType &&
                           dependencyType.GetGenericTypeDefinition() == typeof(IEnumerable<>);
        var serviceType = isEnumerable ? dependencyType.GetGenericArguments()[0] : dependencyType;

        if (serviceType.IsGenericParameter)
        {
            throw UnknowableDependency(handlerType, dependencyType);
        }

        var descriptors = FindDescriptors(services, serviceType);
        if (descriptors.Count == 0)
        {
            // Not registered at all: DI reports that itself. But a dependency whose closed type is only
            // known once the handler is closed, with no open-generic registration to read a lifetime
            // from, cannot be checked up front.
            if (serviceType.ContainsGenericParameters)
            {
                throw UnknowableDependency(handlerType, dependencyType);
            }

            return;
        }

        // Microsoft DI resolves a single service from the LAST registration; an enumerable receives
        // all of them.
        IEnumerable<ServiceDescriptor> relevant = isEnumerable ? descriptors : [descriptors[^1]];
        var offending = relevant.FirstOrDefault(descriptor => descriptor.Lifetime is ServiceLifetime.Scoped or ServiceLifetime.Transient);
        if (offending is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Open-generic request handler '{handlerType.FullName}' is registered as a Singleton, but its " +
            $"constructor depends on '{dependencyType.FullName ?? dependencyType.Name}', which is registered as " +
            $"{offending.Lifetime}. SimpleMediator builds this handler once and reuses it for the whole " +
            "application, so it would capture — and outlive — that dependency, handing out a disposed " +
            "instance to later requests. Use ServiceLifetime.Scoped (or Transient) for this handler, or " +
            "register a closed handler instead.");
    }

    /// <summary>
    /// Registrations that can satisfy <paramref name="serviceType"/>: exact matches, or — for a
    /// constructed generic such as <c>ILogger&lt;Handler&lt;T&gt;&gt;</c> — registrations of its
    /// open-generic definition (<c>ILogger&lt;&gt;</c>). Returned in registration order.
    /// </summary>
    private static List<ServiceDescriptor> FindDescriptors(IServiceCollection services, Type serviceType)
    {
        // A constructor parameter without [FromKeyedServices] is satisfied by non-keyed registrations only.
        var exact = services.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == serviceType).ToList();
        if (exact.Count > 0 || !serviceType.IsGenericType)
        {
            return exact;
        }

        var definition = serviceType.GetGenericTypeDefinition();
        return services.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == definition).ToList();
    }

    private static InvalidOperationException UnknowableDependency(Type handlerType, Type dependencyType)
        => new(
            $"Open-generic request handler '{handlerType.FullName}' is registered as a Singleton, but its " +
            $"constructor depends on the open generic type '{dependencyType.Name}', and no open-generic " +
            "registration exists to read its lifetime from. A singleton custom-mapped open-generic handler " +
            "cannot have a dependency whose lifetime is only known once the type is closed. Register the " +
            "dependency as an open generic, use ServiceLifetime.Scoped (or Transient) for this handler, or " +
            "register it as a closed type.");

    private static void ValidateScannedOpenGenericHandlers(IServiceCollection services)
    {
        var configuration = GetConfiguration(services);
        if (configuration is null)
        {
            return;
        }

        foreach (var registration in configuration.CustomOpenGenericRequestHandlers.DistinctBy(item => item.ImplementationType))
        {
            var openHandler = registration.ImplementationType;
            ValidateOpenGenericImplementation(openHandler);

            if (!OpenGenericMatcher.CanInferOpenGenericRequestHandler(openHandler))
            {
                throw new InvalidOperationException(
                    $"Open-generic request handler '{openHandler.FullName}' has a request/response mapping that cannot be inferred. " +
                    "Every implementation type parameter must appear in the request or response pattern, or the handler must be registered as a closed type.");
            }
        }
    }

    private static void ValidateScannedOpenGenericHandlerConflicts(
        IServiceCollection services,
        IEnumerable<IGrouping<Type, ServiceDescriptor>> requestHandlerGroups)
    {
        var configuration = GetConfiguration(services);
        if (configuration is null)
        {
            return;
        }

        foreach (var group in requestHandlerGroups)
        {
            var typeArguments = group.Key.GetGenericArguments();
            foreach (var registration in configuration.CustomOpenGenericRequestHandlers)
            {
                var openHandler = registration.ImplementationType;
                if (OpenGenericMatcher.TryClose(openHandler, typeArguments[0], typeArguments[1], out _))
                {
                    throw new Core.RequestHandlerResolutionException(
                        $"Request '{typeArguments[0].FullName}' is matched by both a closed handler and the open-generic handler " +
                        $"'{openHandler.FullName}'. A request can only have one handler.");
                }
            }
        }
    }

    private static void ValidateNativeOpenGenericHandlers(
        IServiceCollection services,
        IEnumerable<IGrouping<Type, ServiceDescriptor>> requestHandlerGroups)
    {
        var nativeOpenGenericHandlers = services
            .Where(descriptor => descriptor.ServiceType == typeof(IRequestHandler<,>)
                                 && descriptor.ImplementationType is { IsGenericTypeDefinition: true })
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

        foreach (var group in requestHandlerGroups)
        {
            var typeArguments = group.Key.GetGenericArguments();
            var matchingOpenHandler = nativeOpenGenericHandlers.FirstOrDefault(implementationType =>
                CanCloseNativeOpenGenericHandler(implementationType, typeArguments[0], typeArguments[1]));

            if (matchingOpenHandler is not null)
            {
                throw new Core.RequestHandlerResolutionException(
                    $"Request '{typeArguments[0].FullName}' is matched by both a closed handler and the native open-generic DI handler " +
                    $"'{matchingOpenHandler.FullName}'. A request can only have one handler.");
            }
        }
    }

    private static void ValidateOpenGenericRegistrations(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType is not { IsGenericTypeDefinition: true } implementationType ||
                !descriptor.ServiceType.IsGenericType)
            {
                continue;
            }

            var serviceDefinition = descriptor.ServiceType.GetGenericTypeDefinition();
            if (!OpenGenericRegistrationRules.IsSupportedOpenGenericService(serviceDefinition))
            {
                continue;
            }

            if (!descriptor.ServiceType.ContainsGenericParameters)
            {
                throw new InvalidOperationException(
                    $"Open-generic implementation '{implementationType.FullName}' cannot be registered for closed service '{descriptor.ServiceType.FullName}'.");
            }

            var implementedInterface = OpenGenericRegistrationRules.FindImplementedInterface(
                implementationType,
                serviceDefinition);

            if (implementedInterface is null)
            {
                throw new InvalidOperationException(
                    $"Open-generic implementation '{implementationType.FullName}' does not implement '{serviceDefinition.FullName}'.");
            }

            if (!OpenGenericRegistrationRules.CanRegisterWithNativeResolution(
                    implementationType,
                    implementedInterface,
                    serviceDefinition))
            {
                throw new InvalidOperationException(
                    $"Open-generic handler or behavior '{implementationType.FullName}' cannot be closed by Microsoft DI: " +
                    "its type parameters must line up 1:1 with the service interface, or it must be registered as a closed type.");
            }

            ValidateOpenGenericImplementation(implementationType);
        }
    }

    private static void ValidateOpenGenericImplementation(Type implementationType)
    {
        if (implementationType.IsAbstract || implementationType.IsInterface)
        {
            throw new InvalidOperationException(
                $"Open-generic handler or behavior '{implementationType.FullName}' must be concrete.");
        }

        if (!implementationType.IsValueType &&
            implementationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length == 0)
        {
            throw new InvalidOperationException(
                $"Open-generic handler or behavior '{implementationType.FullName}' must expose a public constructor.");
        }
    }

    private static void ValidateConcreteImplementations(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType is not { } implementationType ||
                implementationType.ContainsGenericParameters ||
                !descriptor.ServiceType.IsGenericType ||
                !OpenGenericRegistrationRules.IsSupportedOpenGenericService(descriptor.ServiceType.GetGenericTypeDefinition()))
            {
                continue;
            }

            if (implementationType.IsAbstract || implementationType.IsInterface)
            {
                throw new InvalidOperationException(
                    $"Handler or behavior implementation '{implementationType.FullName}' must be concrete.");
            }

            if (!descriptor.ServiceType.IsAssignableFrom(implementationType))
            {
                throw new InvalidOperationException(
                    $"Implementation '{implementationType.FullName}' does not implement service '{descriptor.ServiceType.FullName}'.");
            }

            if (!implementationType.IsValueType &&
                implementationType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length == 0)
            {
                throw new InvalidOperationException(
                    $"Handler or behavior implementation '{implementationType.FullName}' must expose a public constructor.");
            }

            try
            {
                _ = ActivatorUtilities.CreateFactory(implementationType, Type.EmptyTypes);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Handler or behavior implementation '{implementationType.FullName}' cannot be activated by Microsoft DI.",
                    exception);
            }
        }
    }

    private static bool CanCloseNativeOpenGenericHandler(Type implementationType, Type requestType, Type responseType)
    {
        if (implementationType.GetGenericArguments().Length != 2)
        {
            return false;
        }

        Type closedImplementation;
        try
        {
            closedImplementation = implementationType.MakeGenericType(requestType, responseType);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var closedService = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
        return closedService.IsAssignableFrom(closedImplementation);
    }

    private static MediatorConfiguration? GetConfiguration(IServiceCollection services)
        => services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(MediatorConfiguration))?
            .ImplementationInstance as MediatorConfiguration;
}
