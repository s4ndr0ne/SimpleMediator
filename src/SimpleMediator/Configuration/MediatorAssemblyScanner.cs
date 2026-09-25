using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

/// <summary>
/// Discovers closed handlers and registers supported open-generic implementations from
/// the assemblies configured by the user.
/// </summary>
internal static class MediatorAssemblyScanner
{
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    internal static List<Type> ScanAndRegister(IServiceCollection services, SimpleMediatorOptions options)
    {
        // Open-generic request handlers are closed on demand because their request type may
        // not line up with Microsoft's native open-generic registration rules.
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
                var loaderErrors = string.Join(Environment.NewLine, ex.LoaderExceptions
                    .Where(error => error is not null)
                    .Select(error => $"- {error!.Message}"));
                throw new InvalidOperationException(
                    $"Unable to scan assembly '{assembly.FullName}'. Resolve the loader errors before registering handlers." +
                    (loaderErrors.Length == 0 ? string.Empty : Environment.NewLine + loaderErrors), ex);
            }

            foreach (var type in types
                         .Where(type => !type.IsAbstract && !type.IsInterface)
                         .OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (type.IsGenericTypeDefinition)
                {
                    RegisterOpenGenericType(services, type, options.DefaultLifetime, openGenericRequestHandlers);
                    continue;
                }

                foreach (var implementedInterface in type.GetInterfaces())
                {
                    if (!implementedInterface.IsGenericType)
                    {
                        continue;
                    }

                    var genericTypeDefinition = implementedInterface.GetGenericTypeDefinition();
                    if (OpenGenericRegistrationRules.MatchesSupportedHandlerInterface(genericTypeDefinition))
                    {
                        services.TryAddEnumerable(new ServiceDescriptor(implementedInterface, type, options.DefaultLifetime));
                    }
                }
            }
        }

        return openGenericRequestHandlers;
    }

    private static void RegisterOpenGenericType(
        IServiceCollection services,
        Type type,
        ServiceLifetime lifetime,
        List<Type> openGenericRequestHandlers)
    {
        var implementsRequestHandler = false;

        foreach (var implementedInterface in type.GetInterfaces())
        {
            if (!implementedInterface.IsGenericType)
            {
                continue;
            }

            var genericTypeDefinition = implementedInterface.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(IRequestHandler<,>))
            {
                implementsRequestHandler = true;
            }

            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(INotificationHandler<>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IPreRequestHandler<,>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IPostRequestHandler<,>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IRequestExceptionHandler<,>), lifetime);
        }

        if (implementsRequestHandler)
        {
            if (!OpenGenericMatcher.CanInferOpenGenericRequestHandler(type))
            {
                throw new InvalidOperationException(
                    $"Open-generic request handler '{type.FullName}' has a request/response mapping that cannot be inferred. " +
                    "Every implementation type parameter must appear in the request or response pattern, or the handler must be registered as a closed type.");
            }

            if (!openGenericRequestHandlers.Contains(type))
            {
                openGenericRequestHandlers.Add(type);
            }
        }
    }

    private static void RegisterNativeOpenGenericInterface(
        IServiceCollection services,
        Type implementationType,
        Type implementedInterface,
        Type serviceTypeDefinition,
        ServiceLifetime lifetime)
    {
        if (!implementedInterface.IsGenericType ||
            implementedInterface.GetGenericTypeDefinition() != serviceTypeDefinition)
        {
            return;
        }

        if (!OpenGenericRegistrationRules.CanRegisterWithNativeResolution(
                implementationType, implementedInterface, serviceTypeDefinition))
        {
            throw new InvalidOperationException(
                $"Open-generic handler '{implementationType.FullName}' implements '{implementedInterface}' but cannot be closed by Microsoft DI. " +
                "The implementation type parameters must line up 1:1 with the service interface, or the handler must be registered as a closed type.");
        }

        services.TryAddEnumerable(new ServiceDescriptor(serviceTypeDefinition, implementationType, lifetime));
    }
}
