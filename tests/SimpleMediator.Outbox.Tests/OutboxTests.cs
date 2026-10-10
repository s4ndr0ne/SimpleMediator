using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;

namespace SimpleMediator.Outbox.Tests;

public sealed class OutboxTests
{
    [Fact]
    public async Task EnqueueAsync_serializes_and_persists_notification()
    {
        var store = new TestOutboxStore();
        var serializer = new TestOutboxSerializer();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer>(serializer);
        services.AddSimpleMediatorOutbox();
        using var provider = services.BuildServiceProvider();

        var notification = new SampleNotification("hello");
        var outbox = provider.GetRequiredService<IOutboxPublisher>();

        await outbox.EnqueueAsync(notification);

        Assert.NotNull(store.Enqueued);
        Assert.Equal(nameof(SampleNotification), store.Enqueued.Type);
        Assert.Equal(notification.Value, serializer.LastSerialized?.Value);
    }

    [Fact]
    public void AddSimpleMediatorOutbox_applies_options_from_repeated_registrations()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorOutbox(options => options.BatchSize = 8);
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(25));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<OutboxOptions>();

        Assert.Equal(8, options.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(25), options.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), options.LeaseRenewalInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RetryDelay);
        Assert.Single(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void AddSimpleMediatorOutbox_rejects_invalid_options_on_resolution()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorOutbox(options => options.BatchSize = 0);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<OutboxOptions>());
    }

    [Fact]
    public void AddSimpleMediatorOutbox_rejects_lease_renewal_interval_not_shorter_than_lease()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorOutbox(options => options.LeaseRenewalInterval = options.LeaseDuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<OutboxOptions>());
    }

    [Fact]
    public async Task Worker_publishes_claimed_message_and_marks_it_processed()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "hello", DateTimeOffset.UtcNow);
        var store = new TestOutboxStore(message);
        var publisher = new TestPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddSingleton<IPublisher>(publisher);
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(5));
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);

        var published = await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var processedId = await store.Processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(new SampleNotification("hello"), published);
        Assert.Equal(message.Id, processedId);
        Assert.NotEmpty(store.RequestedBatchSizes);
        Assert.All(store.RequestedBatchSizes, batchSize => Assert.Equal(1, batchSize));
    }

    [Fact]
    public async Task Concurrent_workers_claim_the_next_message_while_another_worker_is_dispatching()
    {
        var firstMessage = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "first", DateTimeOffset.UtcNow);
        var secondMessage = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "second", DateTimeOffset.UtcNow);
        var store = new ConcurrentOutboxStore([firstMessage, secondMessage]);
        var publisher = new BlockingTestPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddSingleton<IPublisher>(publisher);
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(5));
        using var firstProvider = services.BuildServiceProvider();
        using var secondProvider = services.BuildServiceProvider();

        var firstWorker = firstProvider.GetServices<IHostedService>().Single();
        var secondWorker = secondProvider.GetServices<IHostedService>().Single();
        await firstWorker.StartAsync(CancellationToken.None);

        try
        {
            await publisher.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondWorker.StartAsync(CancellationToken.None);
            var secondStarted = await publisher.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("second", secondStarted);

            publisher.ReleaseFirst.TrySetResult();
            await store.AllProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, store.ProcessedIds.Count);
            Assert.Contains(firstMessage.Id, store.ProcessedIds);
            Assert.Contains(secondMessage.Id, store.ProcessedIds);
            Assert.All(store.RequestedBatchSizes, batchSize => Assert.Equal(1, batchSize));
        }
        finally
        {
            publisher.ReleaseFirst.TrySetResult();
            await firstWorker.StopAsync(CancellationToken.None);
            await secondWorker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_renews_active_lease_while_handler_is_running()
    {
        var leaseDuration = TimeSpan.FromMilliseconds(180);
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "first", DateTimeOffset.UtcNow);
        var store = new LeaseExpiringOutboxStore(message);
        var publisher = new BlockingTestPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddSingleton<IPublisher>(publisher);
        services.AddSimpleMediatorOutbox(options =>
        {
            options.BatchSize = 1;
            options.PollingInterval = TimeSpan.FromMilliseconds(5);
            options.LeaseDuration = leaseDuration;
            options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(25);
        });
        using var firstProvider = services.BuildServiceProvider();
        using var secondProvider = services.BuildServiceProvider();

        var firstWorker = firstProvider.GetServices<IHostedService>().Single();
        var secondWorker = secondProvider.GetServices<IHostedService>().Single();
        await firstWorker.StartAsync(CancellationToken.None);

        try
        {
            await publisher.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.LeaseRenewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondWorker.StartAsync(CancellationToken.None);
            await Task.Delay(leaseDuration + leaseDuration);

            Assert.Equal(1, publisher.FirstAttempts);
            publisher.ReleaseFirst.TrySetResult();
            await store.Processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            publisher.ReleaseFirst.TrySetResult();
            await firstWorker.StopAsync(CancellationToken.None);
            await secondWorker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Store_rejects_updates_from_an_expired_lease_owner()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromSeconds(1);
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "hello", nowUtc);
        var store = new LeaseExpiringOutboxStore(message);
        var firstLease = Assert.Single(await store.ClaimBatchAsync(1, nowUtc, leaseDuration));
        var nextClaimAtUtc = nowUtc.Add(leaseDuration).Add(TimeSpan.FromTicks(1));
        var currentLease = Assert.Single(await store.ClaimBatchAsync(1, nextClaimAtUtc, leaseDuration));

        Assert.NotEqual(firstLease.LeaseToken, currentLease.LeaseToken);
        Assert.False(await store.RenewLeaseAsync(firstLease, nextClaimAtUtc, leaseDuration));
        Assert.False(await store.MarkProcessedAsync(firstLease, nextClaimAtUtc));
        Assert.False(await store.MarkFailedAsync(firstLease, nextClaimAtUtc, "stale owner"));
        Assert.True(await store.RenewLeaseAsync(currentLease, nextClaimAtUtc, leaseDuration));
    }

    [Fact]
    public async Task Worker_keeps_running_and_releases_message_when_dependency_resolution_fails()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "hello", DateTimeOffset.UtcNow);
        var store = new TestOutboxStore(message);
        var publisher = new TestPublisher();
        var publisherResolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddTransient<IPublisher>(_ =>
        {
            if (Interlocked.Increment(ref publisherResolutions) == 1)
            {
                throw new InvalidOperationException("publisher temporarily unavailable");
            }

            return publisher;
        });
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(5));
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            var failed = await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(message.Id, failed.MessageId);
            Assert.Contains("publisher temporarily unavailable", failed.Error, StringComparison.Ordinal);
            Assert.False(publisher.Published.Task.IsCompleted);
            Assert.False(Assert.IsAssignableFrom<BackgroundService>(worker).ExecuteTask!.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_does_not_mark_message_failed_when_mark_processed_fails()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "hello", DateTimeOffset.UtcNow);
        var store = new TestOutboxStore(message, failMarkProcessed: true);
        var publisher = new TestPublisher();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddSingleton<IPublisher>(publisher);
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(5));
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.MarkProcessedAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(store.Failed.Task.IsCompleted);
            Assert.False(Assert.IsAssignableFrom<BackgroundService>(worker).ExecuteTask!.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_releases_message_for_retry_when_publication_fails()
    {
        var message = new OutboxMessage(Guid.NewGuid(), nameof(SampleNotification), "hello", DateTimeOffset.UtcNow);
        var store = new TestOutboxStore(message);
        var publisher = new TestPublisher(new InvalidOperationException("handler failed"));
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IOutboxSerializer, TestOutboxSerializer>();
        services.AddSingleton<IPublisher>(publisher);
        services.AddSimpleMediatorOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(5);
            options.RetryDelay = TimeSpan.FromHours(1);
        });
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);

        var failed = await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(message.Id, failed.MessageId);
        Assert.Contains("handler failed", failed.Error, StringComparison.Ordinal);
        Assert.True(failed.RetryAtUtc > failed.FailedAtUtc);
    }

    private sealed record SampleNotification(string Value) : INotification;

    private sealed class TestOutboxSerializer : IOutboxSerializer
    {
        public SampleNotification? LastSerialized { get; private set; }

        public OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc)
        {
            LastSerialized = Assert.IsType<SampleNotification>(notification);
            return new OutboxMessage(messageId, nameof(SampleNotification), LastSerialized.Value, occurredAtUtc);
        }

        public IOutboxDispatchable Deserialize(OutboxMessage message)
            => new OutboxDispatchable<SampleNotification>(new SampleNotification(message.Payload));
    }

    private sealed class ConcurrentOutboxStore(IEnumerable<OutboxMessage> pending) : IOutboxStore
    {
        private readonly object _gate = new();
        private readonly Queue<OutboxMessage> _pending = new(pending);
        private readonly Dictionary<Guid, OutboxLease> _leases = new();
        private int _processedCount;

        public ConcurrentQueue<int> RequestedBatchSizes { get; } = new();
        public ConcurrentQueue<Guid> ProcessedIds { get; } = new();
        public TaskCompletionSource AllProcessed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            RequestedBatchSizes.Enqueue(batchSize);
            lock (_gate)
            {
                var claimed = new List<OutboxLease>(batchSize);
                while (claimed.Count < batchSize && _pending.TryDequeue(out var message))
                {
                    var lease = new OutboxLease(message, Guid.NewGuid());
                    _leases.Add(message.Id, lease);
                    claimed.Add(lease);
                }

                return Task.FromResult<IReadOnlyList<OutboxLease>>(claimed);
            }
        }

        public Task<bool> RenewLeaseAsync(
            OutboxLease lease,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_leases.TryGetValue(lease.Message.Id, out var current)
                    && current.LeaseToken == lease.LeaseToken);
            }
        }

        public Task<bool> MarkProcessedAsync(OutboxLease lease, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(lease.Message.Id, out var current) || current.LeaseToken != lease.LeaseToken)
                {
                    return Task.FromResult(false);
                }

                _leases.Remove(lease.Message.Id);
                ProcessedIds.Enqueue(lease.Message.Id);
                if (Interlocked.Increment(ref _processedCount) == 2)
                {
                    AllProcessed.TrySetResult();
                }

                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkFailedAsync(
            OutboxLease lease,
            DateTimeOffset retryAtUtc,
            string error,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(lease.Message.Id, out var current) || current.LeaseToken != lease.LeaseToken)
                {
                    return Task.FromResult(false);
                }

                _leases.Remove(lease.Message.Id);
                return Task.FromResult(true);
            }
        }
    }

    private sealed class BlockingTestPublisher : IPublisher
    {
        private int _firstAttempts;

        public TaskCompletionSource<string> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> DuplicateFirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FirstAttempts => Volatile.Read(ref _firstAttempts);

        public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            var value = Assert.IsType<SampleNotification>(notification).Value;
            if (value == "first")
            {
                if (Interlocked.Increment(ref _firstAttempts) == 1)
                {
                    FirstStarted.TrySetResult(value);
                }
                else
                {
                    DuplicateFirstStarted.TrySetResult(value);
                }

                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondStarted.TrySetResult(value);
            }
        }
    }

    private sealed class LeaseExpiringOutboxStore(OutboxMessage message) : IOutboxStore
    {
        private readonly object _gate = new();
        private Guid? _leaseToken;
        private DateTimeOffset? _leaseUntilUtc;
        private DateTimeOffset _availableAtUtc = message.OccurredAtUtc;
        private bool _processed;

        public TaskCompletionSource LeaseRenewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_processed || _availableAtUtc > nowUtc || _leaseUntilUtc > nowUtc)
                {
                    return Task.FromResult<IReadOnlyList<OutboxLease>>([]);
                }

                _leaseToken = Guid.NewGuid();
                _leaseUntilUtc = nowUtc.Add(leaseDuration);
                return Task.FromResult<IReadOnlyList<OutboxLease>>([new OutboxLease(message, _leaseToken.Value)]);
            }
        }

        public Task<bool> RenewLeaseAsync(
            OutboxLease lease,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_leaseToken != lease.LeaseToken || _leaseUntilUtc <= nowUtc)
                {
                    return Task.FromResult(false);
                }

                _leaseUntilUtc = nowUtc.Add(leaseDuration);
                LeaseRenewed.TrySetResult();
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkProcessedAsync(OutboxLease lease, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_leaseToken != lease.LeaseToken)
                {
                    return Task.FromResult(false);
                }

                _processed = true;
                _leaseToken = null;
                _leaseUntilUtc = null;
                Processed.TrySetResult();
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkFailedAsync(
            OutboxLease lease,
            DateTimeOffset retryAtUtc,
            string error,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_leaseToken != lease.LeaseToken)
                {
                    return Task.FromResult(false);
                }

                _leaseToken = null;
                _leaseUntilUtc = null;
                _availableAtUtc = retryAtUtc;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class TestOutboxStore(OutboxMessage? pending = null, bool failMarkProcessed = false) : IOutboxStore
    {
        private readonly OutboxLease? _lease = pending is null ? null : new OutboxLease(pending, Guid.NewGuid());
        private readonly bool _failMarkProcessed = failMarkProcessed;
        private int _claimed;

        public OutboxMessage? Enqueued { get; private set; }
        public List<int> RequestedBatchSizes { get; } = [];
        public TaskCompletionSource<Guid> Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MarkProcessedAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(Guid MessageId, DateTimeOffset RetryAtUtc, string Error, DateTimeOffset FailedAtUtc)> Failed { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Enqueued = message;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            RequestedBatchSizes.Add(batchSize);
            IReadOnlyList<OutboxLease> result = _lease is { } lease && Interlocked.Exchange(ref _claimed, 1) == 0
                ? [lease]
                : Array.Empty<OutboxLease>();
            return Task.FromResult(result);
        }

        public Task<bool> RenewLeaseAsync(
            OutboxLease lease,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_lease?.LeaseToken == lease.LeaseToken);

        public Task<bool> MarkProcessedAsync(OutboxLease lease, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            MarkProcessedAttempted.TrySetResult();
            if (_lease?.LeaseToken != lease.LeaseToken)
            {
                return Task.FromResult(false);
            }

            if (_failMarkProcessed)
            {
                return Task.FromException<bool>(new InvalidOperationException("mark processed failed"));
            }

            Processed.TrySetResult(lease.Message.Id);
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(
            OutboxLease lease,
            DateTimeOffset retryAtUtc,
            string error,
            CancellationToken cancellationToken = default)
        {
            if (_lease?.LeaseToken != lease.LeaseToken)
            {
                return Task.FromResult(false);
            }

            Failed.TrySetResult((lease.Message.Id, retryAtUtc, error, DateTimeOffset.UtcNow));
            return Task.FromResult(true);
        }
    }

    private sealed class TestPublisher(Exception? failure = null) : IPublisher
    {
        public TaskCompletionSource<INotification> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            Published.TrySetResult(notification);
            return Task.CompletedTask;
        }
    }
}
