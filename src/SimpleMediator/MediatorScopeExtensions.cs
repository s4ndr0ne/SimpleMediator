using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator;

/// <summary>
/// Owns a DI scope together with the <see cref="Interfaces.IMediator"/> resolved from it, so
/// long-lived components can mediate a unit of work without capturing the root provider.
/// </summary>
/// <remarks>
/// Resolve <see cref="Interfaces.IMediator"/> from a scope, never from the root provider: a
/// root-owned mediator resolves handlers from the root provider, which turns every scoped service
/// (a <c>DbContext</c>, a unit of work, a tenant context) into a single process-wide instance
/// shared by concurrent requests. See <see cref="SimpleMediatorOptions.RequireScopedMediator"/>.
/// </remarks>
public interface IMediatorScope : IAsyncDisposable, IDisposable
{
    /// <summary>The service provider of the scope owned by this instance.</summary>
    IServiceProvider ServiceProvider { get; }

    /// <summary>The mediator resolved from <see cref="ServiceProvider"/>.</summary>
    Interfaces.IMediator Mediator { get; }
}

/// <summary>
/// Helpers for obtaining a correctly scoped mediator.
/// </summary>
public static class MediatorScopeExtensions
{
    /// <summary>
    /// Creates a DI scope and resolves a mediator from it. Dispose the result when the unit of work
    /// completes, so every scoped dependency the handlers used is released with the scope.
    /// </summary>
    /// <param name="scopeFactory">The scope factory, normally the root provider.</param>
    /// <returns>A handle owning the scope and its mediator.</returns>
    /// <exception cref="Core.MediatorScopeException">
    /// Thrown when <see cref="SimpleMediatorOptions.RequireScopedMediator"/> is enabled and the
    /// resulting mediator would in practice be root-owned.
    /// </exception>
    public static IMediatorScope CreateMediatorScope(this IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var scope = scopeFactory.CreateScope();
        try
        {
            var mediator = scope.ServiceProvider.GetRequiredService<Interfaces.IMediator>();
            return new MediatorScope(scope, mediator);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a DI scope and resolves a mediator from it. This overload exists because the common
    /// call sites hold a statically typed <see cref="IServiceProvider"/> (for example
    /// <c>IServiceProvider.Services</c> on an <c>IHost</c>), which does not expose
    /// <see cref="IServiceScopeFactory"/> even though every Microsoft DI provider implements it.
    /// </summary>
    /// <param name="serviceProvider">A service provider, normally the root provider.</param>
    /// <returns>A handle owning the scope and its mediator.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="serviceProvider"/> does not support creating scopes.
    /// </exception>
    /// <exception cref="Core.MediatorScopeException">
    /// Thrown when <see cref="SimpleMediatorOptions.RequireScopedMediator"/> is enabled and the
    /// resulting mediator would in practice be root-owned.
    /// </exception>
    public static IMediatorScope CreateMediatorScope(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // Microsoft DI does not implement IServiceScopeFactory on the provider type; it exposes it
        // as a registered service. Resolving it is also what proves which provider is the root one.
        var scopeFactory = serviceProvider.GetService<IServiceScopeFactory>();
        if (scopeFactory is null)
        {
            throw new InvalidOperationException(
                $"'{serviceProvider.GetType().FullName}' does not provide '{nameof(IServiceScopeFactory)}', so it cannot " +
                "create the DI scope a mediator needs. SimpleMediator targets Microsoft.Extensions.DependencyInjection.");
        }

        return CreateMediatorScope(scopeFactory);
    }

    private sealed class MediatorScope : IMediatorScope
    {
        private readonly IServiceScope _scope;
        private int _disposed;

        public MediatorScope(IServiceScope scope, Interfaces.IMediator mediator)
        {
            _scope = scope;
            Mediator = mediator;
        }

        public IServiceProvider ServiceProvider => _scope.ServiceProvider;

        public Interfaces.IMediator Mediator { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _scope.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (_scope is IAsyncDisposable asyncDisposableScope)
                {
                    await asyncDisposableScope.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    _scope.Dispose();
                }
            }
        }
    }
}
