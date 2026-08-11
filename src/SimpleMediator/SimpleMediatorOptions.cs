using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

public class SimpleMediatorOptions
{
    internal HashSet<Assembly> Assemblies { get; } = new();
    internal HashSet<Type> Behaviors { get; } = new();
    public ServiceLifetime DefaultLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// How notifications are dispatched to their handlers. Defaults to
    /// <see cref="NotificationPublishStrategy.Sequential"/>, which is safe to share a
    /// scoped service (e.g. <c>DbContext</c>) across handlers. Switch to
    /// <see cref="NotificationPublishStrategy.Parallel"/> only when handlers are
    /// independent and do not share non-thread-safe scoped state.
    /// </summary>
    public NotificationPublishStrategy NotificationPublishStrategy { get; set; } = NotificationPublishStrategy.Sequential;

    /// <summary>
    /// When true, <c>AddSimpleMediator</c> runs <c>ValidateSimpleMediator</c> immediately so
    /// configuration errors (duplicate request handlers, a request matched by both a closed
    /// and an open-generic handler) fail fast at startup instead of on the first request.
    /// Defaults to false.
    /// </summary>
    public bool ValidateOnBuild { get; set; }

    public SimpleMediatorOptions RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

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
