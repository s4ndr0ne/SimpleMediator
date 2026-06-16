using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

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

    /// <summary>
    /// Registers a pipeline behavior. Execution order is controlled by the behavior's
    /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> <c>Order</c> property
    /// (lower runs first / outermost).
    /// </summary>
    public SimpleMediatorOptions AddBehavior(Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        var implementsPipelineBehavior = behaviorType.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!implementsPipelineBehavior)
        {
            throw new ArgumentException(
                $"Type '{behaviorType.FullName}' must implement {typeof(IPipelineBehavior<,>).Name}. " +
                "Register either an open generic type (e.g. typeof(MyBehavior<,>)) or a closed type " +
                "implementing IPipelineBehavior<TRequest, TResponse>.",
                nameof(behaviorType));
        }

        Behaviors.Add(behaviorType);
        return this;
    }
}
