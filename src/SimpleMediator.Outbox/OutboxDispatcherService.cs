using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's hosted-service registration.")]
internal sealed class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcherService>? logger = null) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;
    private readonly ILogger<OutboxDispatcherService> _logger = logger ?? NullLogger<OutboxDispatcherService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatchedAny = false;
            var claimFailed = false;

            for (var dispatchedCount = 0; dispatchedCount < _options.BatchSize; dispatchedCount++)
            {
                IReadOnlyList<OutboxLease> messages;
                try
                {
                    var scope = scopeFactory.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                        messages = await store.ClaimBatchAsync(
                            1,
                            DateTimeOffset.UtcNow,
                            _options.LeaseDuration,
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
                    await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
                    claimFailed = true;
                    break;
                }

                if (messages.Count == 0)
                {
                    break;
                }

                dispatchedAny = true;
                foreach (var lease in messages)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await DispatchAsync(lease, stoppingToken).ConfigureAwait(false);
                }
            }

            if (claimFailed)
            {
                continue;
            }

            if (!dispatchedAny)
            {
                await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(OutboxLease lease, CancellationToken stoppingToken)
    {
        IOutboxStore? store = null;
        try
        {
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var services = scope.ServiceProvider;
                IOutboxSerializer? serializer = null;
                IPublisher? publisher = null;
                try
                {
                    store = services.GetRequiredService<IOutboxStore>();
                    serializer = services.GetRequiredService<IOutboxSerializer>();
                    publisher = services.GetRequiredService<IPublisher>();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    OutboxLog.DispatchSetupFailed(_logger, exception, lease.Message.Id);
                    if (store is null)
                    {
                        await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReleaseFailedLeaseAsync(store, lease, exception).ConfigureAwait(false);
                    }

                    return;
                }

                await DispatchLeasedMessageAsync(lease, store, serializer, publisher, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            OutboxLog.DispatchSetupFailed(_logger, exception, lease.Message.Id);
            await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchLeasedMessageAsync(
        OutboxLease lease,
        IOutboxStore store,
        IOutboxSerializer serializer,
        IPublisher publisher,
        CancellationToken stoppingToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseUntilStoppedAsync(lease, heartbeatStop.Token);
        var ownsLease = true;
        Exception? dispatchFailure = null;

        try
        {
            var dispatchable = serializer.Deserialize(lease.Message)
                ?? throw new InvalidOperationException("The outbox serializer returned null.");
            var publishTask = dispatchable.PublishAsync(publisher, dispatchCancellation.Token);
            if (await Task.WhenAny(publishTask, heartbeat).ConfigureAwait(false) == heartbeat
                && !await heartbeat.ConfigureAwait(false))
            {
                await dispatchCancellation.CancelAsync().ConfigureAwait(false);
            }

            await publishTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            dispatchFailure = exception;
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            ownsLease = await heartbeat.ConfigureAwait(false);
        }

        if (!ownsLease)
        {
            return;
        }

        if (dispatchFailure is not null)
        {
            OutboxLog.DispatchFailed(_logger, dispatchFailure, lease.Message.Id);
            await ReleaseFailedLeaseAsync(store, lease, dispatchFailure).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!await store.MarkProcessedAsync(lease, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false))
            {
                OutboxLog.LeaseLost(_logger, lease.Message.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            OutboxLog.MarkProcessedFailed(_logger, exception, lease.Message.Id);
        }
    }

    private async Task<bool> RenewLeaseUntilStoppedAsync(
        OutboxLease lease,
        CancellationToken heartbeatToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(_options.LeaseRenewalInterval, heartbeatToken).ConfigureAwait(false);
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                    var renewed = await store.RenewLeaseAsync(
                        lease,
                        DateTimeOffset.UtcNow,
                        _options.LeaseDuration,
                        heartbeatToken).ConfigureAwait(false);
                    if (renewed)
                    {
                        continue;
                    }
                }

                OutboxLog.LeaseLost(_logger, lease.Message.Id);
                return false;
            }
            catch (OperationCanceledException) when (heartbeatToken.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception exception)
            {
                OutboxLog.LeaseRenewalFailed(_logger, exception, lease.Message.Id);
                return false;
            }
        }
    }

    private async Task ReleaseFailedLeaseAsync(IOutboxStore store, OutboxLease lease, Exception exception)
    {
        try
        {
            var released = await store.MarkFailedAsync(
                lease,
                DateTimeOffset.UtcNow.Add(_options.RetryDelay),
                exception.ToString(),
                CancellationToken.None).ConfigureAwait(false);
            if (!released)
            {
                OutboxLog.LeaseLost(_logger, lease.Message.Id);
            }
        }
        catch (Exception markFailedException)
        {
            OutboxLog.MarkFailedFailed(_logger, markFailedException, lease.Message.Id);
        }
    }
}
