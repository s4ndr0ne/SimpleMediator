using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

/// <summary>
/// Validates request-handler and open-generic behavior registrations before the service
/// provider is built.
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

        ValidateScannedOpenGenericHandlers(services, requestHandlerGroups);
        ValidateNativeOpenGenericHandlers(services, requestHandlerGroups);
        ValidateOpenGenericBehaviors(services);
    }

    private static void ValidateScannedOpenGenericHandlers(
        IServiceCollection services,
        IEnumerable<IGrouping<Type, ServiceDescriptor>> requestHandlerGroups)
    {
        var configuration = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(MediatorConfiguration))?
            .ImplementationInstance as MediatorConfiguration;

        if (configuration is null || configuration.OpenGenericRequestHandlers.Count == 0)
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

    private static void ValidateOpenGenericBehaviors(IServiceCollection services)
    {
        var openBehaviorTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>)
                                 && descriptor.ImplementationType is { IsGenericTypeDefinition: true })
            .Select(descriptor => descriptor.ImplementationType!);

        foreach (var implementationType in openBehaviorTypes)
        {
            var pipelineInterface = implementationType.GetInterfaces().FirstOrDefault(implementedInterface =>
                implementedInterface.IsGenericType &&
                OpenGenericRegistrationRules.IsPipelineBehaviorDefinition(implementedInterface.GetGenericTypeDefinition()));

            if (pipelineInterface is null ||
                OpenGenericRegistrationRules.CanRegisterWithNativeResolution(
                    implementationType, pipelineInterface, typeof(IPipelineBehavior<,>)))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Open-generic behavior '{implementationType.FullName}' cannot be closed by Microsoft DI: its type parameters " +
                "do not line up 1:1 with IPipelineBehavior<TRequest, TResponse>. Rename them so the service and " +
                "implementation share the same parameter list, or register a closed behavior instead.");
        }
    }
}
