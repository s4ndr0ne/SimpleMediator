using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;
using SimpleMediator;

namespace SimpleMediator.Configuration;

/// <summary>
/// Discovers closed handlers and registers supported open-generic implementations from
/// the assemblies configured by the user.
/// </summary>
internal static class MediatorAssemblyScanner
{
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    internal static List<OpenGenericHandlerRegistration> ScanAndRegister(IServiceCollection services, SimpleMediatorOptions options)
    {
        // Keep only request handlers that need custom type-argument inference. Handlers whose
        // implementation parameters line up with IRequestHandler<TRequest, TResponse> are
        // registered through native DI and inherit its lifetime and decoration semantics.
        var customOpenGenericRequestHandlers = new List<OpenGenericHandlerRegistration>();

        foreach (var assembly in options.Assemblies)
        {
            options.AssemblyFilters.TryGetValue(assembly, out var filter);

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
                if (filter is not null && !filter(type))
                {
                    continue;
                }

                // An open generic nested inside a generic type (Outer<T>.Handler<TU>) can never be
                // closed: no caller can supply Outer<T>'s argument, so neither Microsoft DI nor the
                // custom matcher can produce a usable instance. Skip it rather than failing the whole
                // composition root over a type that is unreachable by design. Plain nested types
                // (Outer.Handler<TU>) stay discoverable.
                if (type.IsGenericTypeDefinition && type.DeclaringType is { ContainsGenericParameters: true })
                {
                    continue;
                }

                if (type.IsGenericTypeDefinition)
                {
                    RegisterOpenGenericType(services, type, options.DefaultLifetime, customOpenGenericRequestHandlers);
                    continue;
                }

                // Any other type that still has unbound generic parameters cannot be instantiated
                // by the container either.
                if (type.ContainsGenericParameters)
                {
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

        return customOpenGenericRequestHandlers;
    }

    private static void RegisterOpenGenericType(
        IServiceCollection services,
        Type type,
        ServiceLifetime lifetime,
        List<OpenGenericHandlerRegistration> customOpenGenericRequestHandlers)
    {
        var implementedInterfaces = type.GetInterfaces()
            .Where(implementedInterface => implementedInterface.IsGenericType)
            .ToArray();

        foreach (var implementedInterface in implementedInterfaces)
        {
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(INotificationHandler<>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IPreRequestHandler<,>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IPostRequestHandler<,>), lifetime);
            RegisterNativeOpenGenericInterface(services, type, implementedInterface, typeof(IRequestExceptionHandler<,>), lifetime);
        }

        var requestHandlerInterfaces = implementedInterfaces
            .Where(implementedInterface =>
                implementedInterface.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .ToArray();

        if (requestHandlerInterfaces.Length == 0)
        {
            return;
        }

        if (requestHandlerInterfaces.All(implementedInterface =>
                OpenGenericRegistrationRules.CanRegisterWithNativeResolution(
                    type,
                    implementedInterface,
                    typeof(IRequestHandler<,>))))
        {
            services.TryAddEnumerable(
                new ServiceDescriptor(typeof(IRequestHandler<,>), type, lifetime));
            return;
        }

        if (!OpenGenericMatcher.CanInferOpenGenericRequestHandler(type))
        {
            throw new InvalidOperationException(
                $"Open-generic request handler '{type.FullName}' has a request/response mapping that cannot be inferred. " +
                "Every implementation type parameter must appear in the request or response pattern, or the handler must be registered as a closed type.");
        }

        if (!customOpenGenericRequestHandlers.Any(registration => registration.ImplementationType == type))
        {
            customOpenGenericRequestHandlers.Add(new OpenGenericHandlerRegistration(type, lifetime));
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
