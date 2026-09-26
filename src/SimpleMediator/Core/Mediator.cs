using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider used to resolve handlers and configuration.</param>
    /// <exception cref="MediatorScopeException">
    /// Thrown when <paramref name="serviceProvider"/> is the application's root provider and
    /// <see cref="SimpleMediatorOptions.RequireScopedMediator"/> is enabled (the default).
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

        // Without AddSimpleMediator there is no configuration singleton to share the caches through,
        // so a directly constructed mediator keeps its own.
        _requestHandlerWrappers = configuration?.RequestHandlerWrappers
            ?? new WrapperCache<(Type Request, Type Response)>();
        _notificationHandlerWrappers = configuration?.NotificationHandlerWrappers
            ?? new WrapperCache<Type>();
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    public async Task Send(IRequest request, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(request);
        await Send<Unit>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ThrowHelper.ThrowIfNull(request);

        var requestType = request.GetType();

        // Key by both request and response type so cache entries remain correct even if callers
        // use explicit generic response types.
        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlerWrappers.GetOrAdd((requestType, typeof(TResponse)), key =>
        {
            // Created once per type and cached; the wrapper is then invoked through typed generic
            // code, so no reflection remains on the per-call path.
            var wrapperType = typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(key.Request, key.Response);
            return Activator.CreateInstance(wrapperType)!;
        });

        return await handler.Handle(request, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
    {
        ThrowHelper.ThrowIfNull(notification);

        var notificationType = notification.GetType();

        var handler = (NotificationHandlerWrapper)_notificationHandlerWrappers.GetOrAdd(notificationType, t =>
        {
            var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
            return Activator.CreateInstance(wrapperType)!;
        });

        await handler.Handle(notification, _serviceProvider, cancellationToken).ConfigureAwait(false);
    }
}
