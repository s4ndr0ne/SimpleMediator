using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Generated;

/// <summary>
/// Compile-time registration table populated by code emitted by the SimpleMediator source generator.
/// </summary>
/// <remarks>
/// Infrastructure for generated code; not intended to be called directly. Every member receives its
/// types as generic arguments, so the whole table is statically visible to the trimmer and to the
/// Native AOT compiler: no handler is discovered and no generic type is closed at runtime.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedMediatorRegistry
{
    internal GeneratedMediatorRegistry()
    {
    }

    internal Dictionary<(Type Request, Type Response), object> RequestWrappers { get; } = new();

    internal Dictionary<Type, object> NotificationWrappers { get; } = new();

    internal HashSet<Assembly> ScannedAssemblies { get; } = new();

    internal List<(Type DeclaredType, Func<ServiceLifetime, ServiceDescriptor> Descriptor)> Handlers { get; } = new();

    internal Dictionary<Type, List<Func<ServiceLifetime, ServiceDescriptor>>> Behaviors { get; } = new();

    /// <summary>
    /// Records that the generator scanned <paramref name="assembly"/> at compile time, so it may be
    /// passed to <see cref="SimpleMediatorOptions.RegisterAssembly(Assembly)"/>.
    /// </summary>
    /// <param name="assembly">The scanned assembly.</param>
    public void AddAssembly(Assembly assembly)
    {
        Infrastructure.ThrowHelper.ThrowIfNull(assembly);
        ScannedAssemblies.Add(assembly);
    }

    /// <summary>
    /// Registers the dispatch wrapper for a request type and one of its response types.
    /// </summary>
    /// <typeparam name="TRequest">The concrete request type.</typeparam>
    /// <typeparam name="TResponse">The response type declared by <see cref="IRequest{TResponse}"/>.</typeparam>
    public void AddRequest<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
        => RequestWrappers[(typeof(TRequest), typeof(TResponse))] = new RequestHandlerWrapperImpl<TRequest, TResponse>();

    /// <summary>
    /// Registers the dispatch wrapper for a concrete notification type.
    /// </summary>
    /// <typeparam name="TNotification">The concrete notification type.</typeparam>
    public void AddNotification<TNotification>()
        where TNotification : INotification
        => NotificationWrappers[typeof(TNotification)] = new NotificationHandlerWrapperImpl<TNotification>();

    /// <summary>
    /// Registers a closed handler (request, notification, pre/post-processor or exception handler)
    /// discovered in a scanned assembly. It is added only when its assembly is passed to
    /// <see cref="SimpleMediatorOptions.RegisterAssembly(Assembly, Func{Type, bool})"/> and the
    /// assembly filter accepts <paramref name="declaredType"/>, with
    /// <see cref="SimpleMediatorOptions.DefaultLifetime"/> as lifetime.
    /// </summary>
    /// <typeparam name="TService">The closed SimpleMediator handler interface.</typeparam>
    /// <typeparam name="TImplementation">The concrete, closed implementation type.</typeparam>
    /// <param name="declaredType">The type as declared in its assembly (the generic definition for a handler closed at compile time), which is what assembly filters observe.</param>
    public void AddHandler<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(Type declaredType)
        where TService : class
        where TImplementation : class, TService
    {
        Infrastructure.ThrowHelper.ThrowIfNull(declaredType);
        Handlers.Add((declaredType, DescriptorFactory<TService, TImplementation>.Create));
    }

    /// <summary>
    /// Registers the closed form of a pipeline behavior for one request/response pair. The closed
    /// descriptor is only added when <paramref name="behaviorType"/> is passed to
    /// <see cref="SimpleMediatorOptions.AddBehavior(Type)"/>, preserving the opt-in, ordered behavior model.
    /// </summary>
    /// <typeparam name="TService">The closed <see cref="IPipelineBehavior{TRequest, TResponse}"/> interface.</typeparam>
    /// <typeparam name="TImplementation">The closed behavior implementation type.</typeparam>
    /// <param name="behaviorType">The behavior type as passed to <c>AddBehavior</c> (open generic definition or closed type).</param>
    public void AddBehavior<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(Type behaviorType)
        where TService : class
        where TImplementation : class, TService
    {
        Infrastructure.ThrowHelper.ThrowIfNull(behaviorType);

        if (!Behaviors.TryGetValue(behaviorType, out var descriptors))
        {
            descriptors = new List<Func<ServiceLifetime, ServiceDescriptor>>();
            Behaviors.Add(behaviorType, descriptors);
        }

        descriptors.Add(DescriptorFactory<TService, TImplementation>.Create);
    }

    private static class DescriptorFactory<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>
        where TImplementation : class, TService
    {
        internal static readonly Func<ServiceLifetime, ServiceDescriptor> Create =
            static lifetime => new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime);
    }
}
