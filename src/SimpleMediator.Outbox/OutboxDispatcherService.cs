using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's hosted-service registration.")]
internal sealed class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    OutboxOptions options,
    ILogger<OutboxDispatcherService>? logger = null) : BackgroundService
{
    private readonly ILogger<OutboxDispatcherService> _logger = logger ?? NullLogger<OutboxDispatcherService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            for (var claimedCount = 0; claimedCount < options.BatchSize; claimedCount++)
            {
                IReadOnlyList<OutboxMessage> messages;
                try
                {
                    var scope = scopeFactory.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                        messages = await store.ClaimBatchAsync(
                            1,
                            DateTimeOffset.UtcNow,
                            options.LeaseDuration,
                            stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    OutboxLog.ClaimBatchFailed(_logger, exception);
                    await Task.Delay(options.PollingInterval, stoppingToken).ConfigureAwait(false);
                    break;
                }

                if (messages.Count == 0)
                {
                    await Task.Delay(options.PollingInterval, stoppingToken).ConfigureAwait(false);
                    break;
                }

                foreach (var message in messages)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await DispatchAsync(message, stoppingToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task DispatchAsync(OutboxMessage message, CancellationToken stoppingToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<IOutboxStore>();
            var serializer = services.GetRequiredService<IOutboxSerializer>();
            var publisher = services.GetRequiredService<IPublisher>();

            try
            {
                var dispatchable = serializer.Deserialize(message)
                    ?? throw new InvalidOperationException("The outbox serializer returned null.");
                await dispatchable.PublishAsync(publisher, stoppingToken).ConfigureAwait(false);
                await store.MarkProcessedAsync(message.Id, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                OutboxLog.DispatchFailed(_logger, exception, message.Id);
                try
                {
                    await store.MarkFailedAsync(
                        message.Id,
                        DateTimeOffset.UtcNow.Add(options.RetryDelay),
                        exception.ToString(),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception markFailedException)
                {
                    OutboxLog.MarkFailedFailed(_logger, markFailedException, message.Id);
                }
            }
        }
    }
}
