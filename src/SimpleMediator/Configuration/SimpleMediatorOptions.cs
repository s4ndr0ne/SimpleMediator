using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator;

/// <summary>
/// Configuration options for SimpleMediator registrations and runtime behavior.
/// </summary>
public class SimpleMediatorOptions
{
    // Keep the registration order stable so assembly scanning and behavior ordering stay
    // deterministic across AddSimpleMediator calls and different runtime implementations.
    internal List<Assembly> Assemblies { get; } = new();
    internal List<Type> Behaviors { get; } = new();
    private ServiceLifetime? _defaultLifetime;
    private NotificationPublishStrategy? _notificationPublishStrategy;
    private int? _openGenericResolutionCacheCapacity;
    private bool? _requireScopedMediator;

    /// <summary>
    /// The default service lifetime used when registering discovered handlers and behaviors in DI.
    /// This also applies to custom-mapped open-generic request handlers, whose instances are
    /// cached and disposed according to this lifetime. Defaults to <see cref="ServiceLifetime.Scoped"/>.
    /// </summary>
    public ServiceLifetime DefaultLifetime
    {
        get => _defaultLifetime ?? ServiceLifetime.Scoped;
        set => _defaultLifetime = value;
    }

    /// <summary>
    /// How notifications are dispatched to their handlers. Defaults to
    /// <see cref="NotificationPublishStrategy.Sequential"/>, which is safe to share a
    /// scoped service (e.g. <c>DbContext</c>) across handlers. Switch to
    /// <see cref="NotificationPublishStrategy.Parallel"/> only when handlers are
    /// independent and do not share non-thread-safe scoped state.
    /// </summary>
    public NotificationPublishStrategy NotificationPublishStrategy
    {
        get => _notificationPublishStrategy ?? NotificationPublishStrategy.Sequential;
        set => _notificationPublishStrategy = value;
    }

    /// <summary>
    /// When true, <c>AddSimpleMediator</c> runs <c>ValidateSimpleMediator</c> immediately so
    /// configuration errors for known closed request registrations (duplicate request
    /// handlers, invalid open-generic mappings, or a closed handler also matched by an
    /// open-generic handler) fail fast at registration time instead of on the first request.
    /// Once enabled for a service collection, subsequent modular <c>AddSimpleMediator</c>
    /// calls keep validation enabled. It cannot validate request types that have no closed
    /// handler registration. Defaults to false.
    /// </summary>
    public bool ValidateOnBuild { get; set; }

    /// <summary>
    /// Maximum number of open-generic request resolution plans retained per
    /// mediator configuration. Plans contain factories, never handler instances.
    /// </summary>
    public int OpenGenericResolutionCacheCapacity
    {
        get => _openGenericResolutionCacheCapacity ?? 1024;
        set => _openGenericResolutionCacheCapacity = value;
    }

    internal bool HasCustomPublishStrategy => _notificationPublishStrategy.HasValue;
    internal bool HasCustomCacheCapacity => _openGenericResolutionCacheCapacity.HasValue;
    internal bool HasCustomRequireScopedMediator => _requireScopedMediator.HasValue;

    /// <summary>
    /// Per-assembly discovery filters registered through
    /// <see cref="RegisterAssembly(Assembly, Func{Type, bool})"/>. An entry with a
    /// <c>null</c> value means "no filter".
    /// </summary>
    internal Dictionary<Assembly, Func<Type, bool>?> AssemblyFilters { get; } = new();

    /// <summary>
    /// When <c>true</c> (the default), resolving <c>IMediator</c> from the application's
    /// <em>root</em> service provider throws <see cref="Core.MediatorScopeException"/> instead of
    /// silently degrading every scoped service to a process-wide singleton.
    /// <para>
    /// Resolve the mediator inside the request or operation scope. When a long-lived component
    /// (background service, queue consumer, hosted service) needs a mediator, create one scope per
    /// unit of work and resolve the mediator from that scope.
    /// </para>
    /// <para>
    /// Set this to <c>false</c> only when root-owned dispatch is deliberate and every handler
    /// dependency is itself a singleton.
    /// </para>
    /// <para>
    /// With modular registration (several <c>AddSimpleMediator</c> calls) a call that does not set
    /// this property keeps the value configured by earlier calls. Two calls that set it explicitly to
    /// <em>different</em> values are a configuration error: one module cannot silently disable the
    /// guard for the others.
    /// </para>
    /// </summary>
    public bool RequireScopedMediator
    {
        get => _requireScopedMediator ?? true;
        set => _requireScopedMediator = value;
    }

    /// <summary>
    /// Registers an assembly to scan for request handlers, notification handlers, pre/post processors,
    /// and request exception handlers. Pipeline behaviors must be registered explicitly with
    /// <see cref="AddBehavior(Type)"/>.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>This options instance for chaining.</returns>
    public SimpleMediatorOptions RegisterAssembly(Assembly assembly)
        => RegisterAssembly(assembly, filter: null);

    /// <summary>
    /// Registers an assembly to scan, optionally excluding types from discovery.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="filter">
    /// A predicate evaluated for every candidate type in <paramref name="assembly"/>. Returning
    /// <c>false</c> skips the type, so it is never registered. Use it to exclude generated,
    /// obsolete, or composition-root types from discovery.
    /// </param>
    /// <returns>This options instance for chaining.</returns>
    public SimpleMediatorOptions RegisterAssembly(Assembly assembly, Func<Type, bool>? filter)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!Assemblies.Contains(assembly))
        {
            Assemblies.Add(assembly);
        }

        // A later call for the same assembly narrows the accepted set rather than replacing the
        // previous predicate, so modular composition never silently re-admits excluded types.
        if (!AssemblyFilters.TryGetValue(assembly, out var existing))
        {
            AssemblyFilters[assembly] = filter;
        }
        else if (filter is not null)
        {
            AssemblyFilters[assembly] = existing is null ? filter : (type => existing(type) && filter(type));
        }

        return this;
    }

    /// <summary>
    /// Registers a pipeline behavior. Execution order is controlled by the behavior's
    /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> <c>Order</c> property
    /// (lower runs first / outermost); equal values run in registration order (FIFO):
    /// the first registered behavior is outermost and runs first.
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

        if (behaviorType.IsAbstract || behaviorType.IsInterface ||
            (behaviorType.ContainsGenericParameters && !behaviorType.IsGenericTypeDefinition))
        {
            throw new ArgumentException(
                $"Behavior type '{behaviorType.FullName}' must be a concrete, fully closed type or an open generic type definition.",
                nameof(behaviorType));
        }

        if (behaviorType.IsGenericTypeDefinition)
        {
            var pipelineInterface = OpenGenericRegistrationRules.FindImplementedInterface(
                behaviorType,
                typeof(IPipelineBehavior<,>));

            if (pipelineInterface is null ||
                !OpenGenericRegistrationRules.CanRegisterWithNativeResolution(
                    behaviorType,
                    pipelineInterface,
                    typeof(IPipelineBehavior<,>)))
            {
                throw new ArgumentException(
                    $"Open-generic behavior '{behaviorType.FullName}' cannot be closed by Microsoft DI: " +
                    "its type parameters must line up 1:1 with IPipelineBehavior<TRequest, TResponse>.",
                    nameof(behaviorType));
            }
        }

        if (!Behaviors.Contains(behaviorType))
        {
            Behaviors.Add(behaviorType);
        }

        return this;
    }
}
