using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox.Tests;

public sealed class OutboxBackoffTests
{
    [Fact]
    public async Task Dispatcher_applies_exponential_backoff_capped_at_max_retry_delay()
    {
        var store = new RetryingStore();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestSerializer>();
        services.AddSingleton<IPublisher, FailingPublisher>();
        services.AddSimpleMediatorOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(1);
            options.RetryDelay = TimeSpan.FromSeconds(10);
            options.RetryBackoffMultiplier = 2;
            options.MaxRetryDelay = TimeSpan.FromSeconds(25);
            options.RetryJitterFactor = 0;
        });
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.ReachedThreeFailures.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, store.RetryDelays.Count);
        Assert.InRange(store.RetryDelays[0], TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11));
        Assert.InRange(store.RetryDelays[1], TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(21));
        Assert.InRange(store.RetryDelays[2], TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(26));
    }

    [Fact]
    public async Task Dispatcher_applies_jitter_within_the_configured_fraction()
    {
        var store = new RetryingStore();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestSerializer>();
        services.AddSingleton<IPublisher, FailingPublisher>();
        services.AddSimpleMediatorOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(1);
            options.RetryDelay = TimeSpan.FromSeconds(10);
            options.RetryBackoffMultiplier = 1;
            options.MaxRetryDelay = TimeSpan.FromMinutes(1);
            options.RetryJitterFactor = 0.2;
        });
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.ReachedThreeFailures.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.All(store.RetryDelays, delay =>
            Assert.InRange(delay, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void AddSimpleMediatorOutbox_rejects_invalid_retry_options()
    {
        AssertInvalid(options => options.RetryBackoffMultiplier = 0.5);
        AssertInvalid(options => options.MaxRetryDelay = TimeSpan.Zero);
        AssertInvalid(options => options.RetryJitterFactor = 1.5);
        AssertInvalid(options => options.RetryJitterFactor = -0.1);
    }

    private static void AssertInvalid(Action<OutboxOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorOutbox(configure);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<OutboxOptions>());
    }

    private sealed class RetryingStore : IOutboxStore
    {
        private int _attempts;
        private OutboxLease? _lease;

        public List<TimeSpan> RetryDelays { get; } = [];

        public TaskCompletionSource ReachedThreeFailures { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            if (_attempts >= 3)
            {
                return Task.FromResult<IReadOnlyList<OutboxLease>>([]);
            }

            _lease = new OutboxLease(
                new OutboxMessage(Guid.NewGuid(), "test.notification", "{}", nowUtc),
                Guid.NewGuid())
            {
                Attempts = _attempts,
            };

            return Task.FromResult<IReadOnlyList<OutboxLease>>([_lease]);
        }

        public Task<bool> RenewLeaseAsync(
            OutboxLease lease,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_lease?.LeaseToken == lease.LeaseToken);

        public Task<bool> MarkProcessedAsync(
            OutboxLease lease,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> MarkFailedAsync(
            OutboxLease lease,
            DateTimeOffset retryAtUtc,
            string error,
            CancellationToken cancellationToken = default)
        {
            RetryDelays.Add(retryAtUtc - DateTimeOffset.UtcNow);
            _attempts++;
            if (_attempts == 3)
            {
                ReachedThreeFailures.TrySetResult();
            }

            return Task.FromResult(true);
        }
    }

    private sealed record TestNotification : INotification;

    private sealed class TestSerializer : IOutboxSerializer
    {
        public OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc)
            => new(messageId, "test.notification", "{}", occurredAtUtc);

        public IOutboxDispatchable Deserialize(OutboxMessage message)
            => new OutboxDispatchable<TestNotification>(new TestNotification());
    }

    private sealed class FailingPublisher : IPublisher
    {
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.FromException(new InvalidOperationException("handler failed"));
    }
}
