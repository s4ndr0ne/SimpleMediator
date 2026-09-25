using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

public class SimpleMediatorOptions
{
    // Keep the registration order stable so assembly scanning and behavior ordering stay
    // deterministic across AddSimpleMediator calls and different runtime implementations.
    internal List<Assembly> Assemblies { get; } = new();
    internal List<Type> Behaviors { get; } = new();
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
    /// configuration errors for known closed request registrations (duplicate request
    /// handlers, or a closed handler also matched by an open-generic handler) fail fast at
    /// registration time instead of on the first request. It cannot validate request types
    /// that have no closed handler registration. Defaults to false.
    /// </summary>
    public bool ValidateOnBuild { get; set; }

    /// <summary>
    /// Maximum number of open-generic request resolution plans retained per
    /// mediator configuration. Plans contain factories, never handler instances.
    /// </summary>
    public int OpenGenericResolutionCacheCapacity { get; set; } = 1024;

    public SimpleMediatorOptions RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!Assemblies.Contains(assembly))
        {
            Assemblies.Add(assembly);
        }

        return this;
    }

    /// <summary>
    /// Registers a pipeline behavior. Execution order is controlled by the behavior's
    /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> <c>Order</c> property
    /// (lower runs first / outermost); equal values preserve DI resolution order.
    /// </summary>
    public SimpleMediatorOptions AddBehavior(Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        var implementsPipelineBehavior = OpenGenericRegistrationRules.ImplementsPipelineBehavior(behaviorType);

        if (!implementsPipelineBehavior)
        {
            throw new ArgumentException(
                $"Type '{behaviorType.FullName}' must implement {typeof(IPipelineBehavior<,>).Name}. " +
                "Register either an open generic type (e.g. typeof(MyBehavior<,>)) or a closed type " +
                "implementing IPipelineBehavior<TRequest, TResponse>.",
                nameof(behaviorType));
        }

        if (!Behaviors.Contains(behaviorType))
        {
            Behaviors.Add(behaviorType);
        }

        return this;
    }
}
