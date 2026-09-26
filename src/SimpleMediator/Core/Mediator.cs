using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

/// <summary>
/// Default implementation of <see cref="IMediator"/> using Microsoft DI.
/// </summary>
public class Mediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;
    // Wrapper caches belong to the DI configuration. This avoids a process-wide static cache
    // retaining types from collectible plugin AssemblyLoadContexts.
    private readonly WrapperCache<(Type Request, Type Response)> _requestHandlerWrappers;
    private readonly WrapperCache<Type> _notificationHandlerWrappers;
    private readonly Func<(Type Request, Type Response), object> _requestWrapperFactory;
    private readonly Func<Type, object> _notificationWrapperFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers and configuration.</param>
    /// <remarks>
    /// The mediator takes its dispatch strategy from the SimpleMediator configuration registered in
    /// <paramref name="serviceProvider"/>: a container composed with <c>AddSimpleMediatorGenerated</c>
    /// dispatches through the source-generated table (trim/AOT-safe), one composed with
    /// <c>AddSimpleMediator</c> through reflection. A provider without any SimpleMediator registration
    /// falls back to reflection, which is not available under Native AOT; call
    /// <c>AddSimpleMediatorGenerated()</c> (it registers no handler by itself) to use a directly
    /// constructed mediator with manually registered handlers in an AOT application.
    /// </remarks>
    /// <exception cref="MediatorScopeException">
    /// Thrown when <paramref name="serviceProvider"/> is the application's root provider and
    /// <see cref="SimpleMediatorOptions.RequireScopedMediator"/> is enabled (the default).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown under Native AOT when <paramref name="serviceProvider"/> has no SimpleMediator registration,
    /// so no dispatch wrapper could be created.
    /// </exception>
    public Mediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        var configuration = serviceProvider.GetService<MediatorConfiguration>();

        // A mediator resolved from the root provider keeps using the root provider forever, so
        // every scoped service it touches (DbContext, unit of work, tenant context) collapses into
        // one process-wide instance shared by concurrent requests. Fail fast instead of letting
        // that happen silently in production.
        if (configuration is { RequireScopedMediator: true } &&
            ServiceProviderScopeFacts.IsRootProvider(serviceProvider))
        {
            throw new MediatorScopeException(
                "SimpleMediator resolved 'IMediator' from the root service provider. A root-owned mediator " +
                "resolves handlers from the root provider, which turns every scoped service (for example a " +
                "DbContext or a unit of work) into a single process-wide instance shared by concurrent requests. " +
                "Resolve 'IMediator' inside the request or operation scope instead, for example: " +
                "'using var scope = rootProvider.CreateScope(); var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();'. " +
                "To accept this trade-off explicitly, set 'SimpleMediatorOptions.RequireScopedMediator = false'.");
        }

#if NET
        // Without any registration the only way to build wrappers is reflection, and under Native AOT
        // that fails on the first value-type request or response with an opaque NotSupportedException.
        // Fail here instead, with the fix.
        if (configuration is null && !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException(
                "SimpleMediator 'Mediator' was constructed from a service provider without any SimpleMediator registration. " +
                "Without a registration it can only build dispatch wrappers through reflection, which Native AOT does not support. " +
                "Call 'services.AddSimpleMediatorGenerated()' (from the s4ndr0ne.SimpleMediator.SourceGenerator package) when " +
                "building the container; it registers the source-generated dispatch table and no handler, so manually " +
                "registered handlers keep working.");
        }
#endif

        // Without AddSimpleMediator there is no configuration singleton to share the caches through,
        // so a directly constructed mediator keeps its own.
        _requestHandlerWrappers = configuration?.RequestHandlerWrappers
            ?? new WrapperCache<(Type Request, Type Response)>();
        _notificationHandlerWrappers = configuration?.NotificationHandlerWrappers
            ?? new WrapperCache<Type>();

        // The configuration decides how a missing wrapper is built: from the source-generated table
        // (trim/AOT-safe), or through reflection for the reflection-based registration. Only a provider
        // without any SimpleMediator registration has no configuration; it uses reflection on JIT runtimes.
        _requestWrapperFactory = configuration?.RequestWrapperFactory ?? ReflectionWrapperFactory.Request;
        _notificationWrapperFactory = configuration?.NotificationWrapperFactory ?? ReflectionWrapperFactory.Notification;
    }

    /// <inheritdoc />
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(request);
        await Send<Unit>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(request);

        var requestType = request.GetType();

        // Key by both request and response type so cache entries remain correct even if callers
        // use explicit generic response types.
        // Wrappers are created once per type and cached; the wrapper is then invoked through typed
        // generic code, so nothing type-dependent is computed on the per-call path.
        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlerWrappers.GetOrAdd(
            (requestType, typeof(TResponse)),
            _requestWrapperFactory);

        return await handler.Handle(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ThrowHelper.ThrowIfNull(notification);

        var notificationType = notification.GetType();

        var handler = (NotificationHandlerWrapper)_notificationHandlerWrappers.GetOrAdd(notificationType, _notificationWrapperFactory);

        await handler.Handle(notification, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }
}
