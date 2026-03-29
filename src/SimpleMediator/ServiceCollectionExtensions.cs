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
            var types = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface);

            foreach (var type in types)
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

        foreach (var behavior in options.Behaviors.OrderBy(b => b.Order))
        {
            services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), behavior.BehaviorType, options.DefaultLifetime));
        }

        return services;
    }
}
