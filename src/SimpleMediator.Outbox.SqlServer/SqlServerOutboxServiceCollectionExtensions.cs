using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SimpleMediator.Outbox.SqlServer;

/// <summary>Registers the SQL Server <see cref="IOutboxStore"/> implementation for <c>SimpleMediator.Outbox</c>.</summary>
public static class SqlServerOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerOutboxStore"/> and <see cref="ISqlServerOutboxSession"/>.
    /// Call <c>AddSimpleMediatorOutbox</c> separately to register the outbox publisher and dispatcher.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the SQL Server outbox options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddSqlServerOutboxStore(
        this IServiceCollection services,
        Action<SqlServerOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SqlServerOutboxOptions>()
            .Configure(configure)
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "ConnectionString must be provided.")
            .Validate(
                static options => SqlServerOutboxSql.IsValidIdentifier(options.SchemaName),
                "SchemaName must be a valid SQL Server identifier.")
            .Validate(
                static options => SqlServerOutboxSql.IsValidTableName(options.TableName),
                "TableName must be a valid SQL Server identifier of at most 100 characters.")
            .Validate(
                static options => options.MaxAttempts > 0 && options.MaxAttempts <= 1000,
                "MaxAttempts must be between 1 and 1000.")
            .Validate(
                static options => options.CommandTimeout >= TimeSpan.FromSeconds(1)
                    && options.CommandTimeout <= TimeSpan.FromSeconds(int.MaxValue - 1),
                "CommandTimeout must be between one second and the maximum command timeout.")
            .Validate(
                static options => options.ProcessedMessageRetention is null || options.ProcessedMessageRetention > TimeSpan.Zero,
                "ProcessedMessageRetention must be null or greater than zero.")
            .Validate(
                static options => options.DeadLetterRetention is null || options.DeadLetterRetention > TimeSpan.Zero,
                "DeadLetterRetention must be null or greater than zero.")
            .Validate(
                static options => options.PurgeInterval > TimeSpan.Zero,
                "PurgeInterval must be greater than zero.")
            .Validate(
                static options => options.PurgeBatchSize is > 0 and <= 10_000,
                "PurgeBatchSize must be between 1 and 10000.")
            .Validate(
                static options => options.MaxPurgeBatchesPerCycle > 0,
                "MaxPurgeBatchesPerCycle must be greater than zero.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISqlServerOutboxSession, SqlServerOutboxSession>();
        services.TryAddScoped<SqlServerOutboxStore>();
        services.TryAddScoped<IOutboxStore>(static provider => provider.GetRequiredService<SqlServerOutboxStore>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerOutboxSchemaHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerOutboxPurgeHostedService>());
        return services;
    }
}
