using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator;

public class SimpleMediatorOptions
{
    internal List<Assembly> Assemblies { get; } = new();
    internal List<Type> Behaviors { get; } = new();
    public ServiceLifetime DefaultLifetime { get; set; } = ServiceLifetime.Scoped;

    public SimpleMediatorOptions RegisterAssembly(Assembly assembly)
    {
        Assemblies.Add(assembly);
        return this;
    }

    public SimpleMediatorOptions AddBehavior(Type behaviorType)
    {
        Behaviors.Add(behaviorType);
        return this;
    }
}
