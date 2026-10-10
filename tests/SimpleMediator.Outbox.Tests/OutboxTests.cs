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
        private int _processedCount;

        public ConcurrentQueue<int> RequestedBatchSizes { get; } = new();
        public ConcurrentQueue<Guid> ProcessedIds { get; } = new();
        public TaskCompletionSource AllProcessed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            RequestedBatchSizes.Enqueue(batchSize);
            lock (_gate)
            {
                var claimed = new List<OutboxMessage>(batchSize);
                while (claimed.Count < batchSize && _pending.TryDequeue(out var message))
                {
                    claimed.Add(message);
                }

                return Task.FromResult<IReadOnlyList<OutboxMessage>>(claimed);
            }
        }

        public Task MarkProcessedAsync(Guid messageId, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            ProcessedIds.Enqueue(messageId);
            if (Interlocked.Increment(ref _processedCount) == 2)
            {
                AllProcessed.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, DateTimeOffset retryAtUtc, string error, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class BlockingTestPublisher : IPublisher
    {
        public TaskCompletionSource<string> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            var value = Assert.IsType<SampleNotification>(notification).Value;
            if (value == "first")
            {
                FirstStarted.TrySetResult(value);
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondStarted.TrySetResult(value);
            }
        }
    }

    private sealed class TestOutboxStore(OutboxMessage? pending = null) : IOutboxStore
    {
        private int _claimed;

        public OutboxMessage? Enqueued { get; private set; }
        public List<int> RequestedBatchSizes { get; } = [];
        public TaskCompletionSource<Guid> Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(Guid MessageId, DateTimeOffset RetryAtUtc, string Error, DateTimeOffset FailedAtUtc)> Failed { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Enqueued = message;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            RequestedBatchSizes.Add(batchSize);
            IReadOnlyList<OutboxMessage> result = pending is not null && Interlocked.Exchange(ref _claimed, 1) == 0
                ? [pending]
                : Array.Empty<OutboxMessage>();
            return Task.FromResult(result);
        }

        public Task MarkProcessedAsync(Guid messageId, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
        {
            Processed.TrySetResult(messageId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, DateTimeOffset retryAtUtc, string error, CancellationToken cancellationToken = default)
        {
            Failed.TrySetResult((messageId, retryAtUtc, error, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
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
