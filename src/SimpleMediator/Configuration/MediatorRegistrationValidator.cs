using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

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
            var constructor = handlerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                                   .OrderByDescending(candidate => candidate.GetParameters().Length)
                                   .FirstOrDefault();
            if (constructor is null)
            {
                continue;
            }

            foreach (var parameter in constructor.GetParameters())
            {
                // A generic parameter (for example TDependency) can only be closed later, so its
                // lifetime cannot be checked here. Such a handler must be registered as Scoped.
                if (parameter.ParameterType.ContainsGenericParameters)
                {
                    throw new InvalidOperationException(
                        $"Open-generic request handler '{handlerType.FullName}' is registered as a Singleton, but its " +
                        $"constructor depends on the open generic type '{parameter.ParameterType.Name}'. A singleton " +
                        "custom-mapped open-generic handler cannot have a lifetime that is only known once the type is " +
                        "closed. Use ServiceLifetime.Scoped (or Transient) for this handler, or register it as a closed type.");
                }

                var descriptor = services.FirstOrDefault(candidate =>
                    candidate.ServiceType == parameter.ParameterType);
                if (descriptor is null)
                {
                    continue;
                }

                if (descriptor.Lifetime is ServiceLifetime.Scoped or ServiceLifetime.Transient)
                {
                    throw new InvalidOperationException(
                        $"Open-generic request handler '{handlerType.FullName}' is registered as a Singleton, but its " +
                        $"constructor depends on '{parameter.ParameterType.FullName}', which is registered as " +
                        $"{descriptor.Lifetime}. SimpleMediator builds this handler once and reuses it for the whole " +
                        "application, so it would capture — and outlive — that dependency, handing out a disposed " +
                        "instance to later requests. Use ServiceLifetime.Scoped (or Transient) for this handler, or " +
                        "register a closed handler instead.");
                }
            }
        }
    }

    /// <summary>
    /// Validates that closed request handlers do not conflict with native open-generic DI
    /// registrations. Behavior lifetime conflicts are detected earlier, in
    /// <see cref="ServiceCollectionExtensions.AddBehavior"/>, because <c>TryAddEnumerable</c> would
    /// otherwise discard the second descriptor before any validator could see it.
    /// </summary>

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
