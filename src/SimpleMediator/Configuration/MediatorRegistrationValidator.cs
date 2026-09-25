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
                throw new InvalidOperationException(
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
    }

    private static void ValidateScannedOpenGenericHandlers(IServiceCollection services)
    {
        var configuration = GetConfiguration(services);
        if (configuration is null)
        {
            return;
        }

        foreach (var openHandler in configuration.OpenGenericRequestHandlers.Distinct())
        {
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
                throw new InvalidOperationException(
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
