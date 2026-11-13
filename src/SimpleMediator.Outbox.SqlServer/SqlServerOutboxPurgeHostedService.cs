using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SimpleMediator.Outbox.SqlServer;

/// <summary>Periodically deletes processed and dead-lettered messages so the outbox table does not grow forever.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's hosted-service registration.")]
internal sealed class SqlServerOutboxPurgeHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SqlServerOutboxOptions> options,
    ILogger<SqlServerOutboxPurgeHostedService>? logger = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly SqlServerOutboxOptions _options = options.Value;
    private readonly ILogger<SqlServerOutboxPurgeHostedService> _logger =
        logger ?? NullLogger<SqlServerOutboxPurgeHostedService>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ProcessedMessageRetention is null && _options.DeadLetterRetention is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.PurgeInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var store = scope.ServiceProvider.GetRequiredService<SqlServerOutboxStore>();
                    var purged = await store.PurgeAsync(stoppingToken).ConfigureAwait(false);
                    if (purged > 0)
                    {
                        SqlServerOutboxLog.PurgedMessages(_logger, purged);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SqlServerOutboxLog.PurgeFailed(_logger, exception);
            }
        }
    }
}
