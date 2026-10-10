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
    /// <param name="configure">Optional dispatcher configuration. May be supplied by repeated calls; each delegate runs in registration order.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddSimpleMediatorOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<OutboxOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        builder.Validate(
            static options => options.BatchSize > 0,
            "BatchSize must be greater than zero.");
        builder.Validate(
            static options => options.PollingInterval > TimeSpan.Zero,
            "PollingInterval must be greater than zero.");
        builder.Validate(
            static options => options.LeaseDuration > TimeSpan.Zero,
            "LeaseDuration must be greater than zero.");
        builder.Validate(
            static options => options.LeaseRenewalInterval > TimeSpan.Zero
                && options.LeaseRenewalInterval < options.LeaseDuration,
            "LeaseRenewalInterval must be greater than zero and less than LeaseDuration.");
        builder.Validate(
            static options => options.RetryDelay >= TimeSpan.Zero,
            "RetryDelay cannot be negative.");

        services.TryAddSingleton(static provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>().Value);
        services.TryAddScoped<IOutboxPublisher, OutboxPublisher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxDispatcherService>());
        return services;
    }
}
