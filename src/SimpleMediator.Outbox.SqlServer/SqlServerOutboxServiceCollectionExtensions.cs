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
                static options => SqlServerOutboxSql.IsValidIdentifier(options.TableName),
                "TableName must be a valid SQL Server identifier.")
            .Validate(
                static options => options.MaxAttempts > 0,
                "MaxAttempts must be greater than zero.")
            .Validate(
                static options => options.CommandTimeout > TimeSpan.Zero,
                "CommandTimeout must be greater than zero.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISqlServerOutboxSession, SqlServerOutboxSession>();
        services.TryAddScoped<SqlServerOutboxStore>();
        services.TryAddScoped<IOutboxStore>(static provider => provider.GetRequiredService<SqlServerOutboxStore>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerOutboxSchemaHostedService>());
        return services;
    }
}
