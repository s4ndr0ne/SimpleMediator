using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SimpleMediator.Outbox.SqlServer;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's hosted-service registration.")]
internal sealed class SqlServerOutboxSchemaHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SqlServerOutboxOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnsureSchemaOnStartup)
        {
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<SqlServerOutboxStore>();
            await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
