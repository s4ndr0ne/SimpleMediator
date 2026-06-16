using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSimpleMediator(this IServiceCollection services, Action<SimpleMediatorOptions> configure)
    {
        var options = new SimpleMediatorOptions();
        configure(options);

       services.Add(new ServiceDescriptor(typeof(IMediator), typeof(Mediator), options.DefaultLifetime));

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

            var filteredTypes = types
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition);

            foreach (var type in filteredTypes)
            {
                var interfaces = type.GetInterfaces();
                foreach (var @interface in interfaces)
                {
                    if (@interface.IsGenericType)
                    {
                        var genericTypeDefinition = @interface.GetGenericTypeDefinition();
                        if (genericTypeDefinition == typeof(IRequestHandler<,>) || genericTypeDefinition == typeof(INotificationHandler<>) ||
                            genericTypeDefinition == typeof(IPreRequestHandler<,>) || genericTypeDefinition == typeof(IPostRequestHandler<,>))
                        {
                            services.Add(new ServiceDescriptor(@interface, type, options.DefaultLifetime));
                        }
                    }
                }
            }
        }

        // Execution order is determined by each behavior's Order property at request time
        // (see RequestHandlerWrapperImpl), so registration order here is irrelevant.
        foreach (var behaviorType in options.Behaviors)
        {
            if (behaviorType.IsGenericTypeDefinition)
            {
                services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), behaviorType, options.DefaultLifetime));
            }
            else
            {
                // Closed/concrete behavior: register it against each closed IPipelineBehavior<,>
                // interface it implements so it can be resolved for those specific request types.
                var closedInterfaces = behaviorType.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

                foreach (var closedInterface in closedInterfaces)
                {
                    services.Add(new ServiceDescriptor(closedInterface, behaviorType, options.DefaultLifetime));
                }
            }
        }

        return services;
    }
}
