using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SimpleMediator.Outbox;

/// <summary>Registers the provider-agnostic SimpleMediator outbox publisher and hosted dispatcher.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers outbox services. The application must register <see cref="IOutboxStore"/> and
    /// <see cref="IOutboxSerializer"/> implementations, and must call <c>AddSimpleMediator</c> separately.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional dispatcher configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddSimpleMediatorOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OutboxOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddScoped<IOutboxPublisher, OutboxPublisher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxDispatcherService>());
        return services;
    }
}
