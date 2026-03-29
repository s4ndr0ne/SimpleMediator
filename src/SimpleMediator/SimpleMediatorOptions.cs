using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator;

public class SimpleMediatorOptions
{
    internal List<Assembly> Assemblies { get; } = new();
    internal List<BehaviorRegistration> Behaviors { get; } = new();
    public ServiceLifetime DefaultLifetime { get; set; } = ServiceLifetime.Scoped;

    public SimpleMediatorOptions RegisterAssembly(Assembly assembly)
    {
        Assemblies.Add(assembly);
        return this;
    }

    public SimpleMediatorOptions AddBehavior(Type behaviorType, int order = 0)
    {
        Behaviors.Add(new BehaviorRegistration(behaviorType, order));
        return this;
    }
}

public record BehaviorRegistration(Type BehaviorType, int Order)
{
    public int Order { get; } = Order;
    public Type BehaviorType { get; } = BehaviorType;
}
