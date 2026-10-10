using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(5));
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetServices<IHostedService>().Single();
        await worker.StartAsync(CancellationToken.None);

        var failed = await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(message.Id, failed.MessageId);
        Assert.Contains("handler failed", failed.Error, StringComparison.Ordinal);
        Assert.True(failed.RetryAtUtc > DateTimeOffset.UtcNow);
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

    private sealed class TestOutboxStore(OutboxMessage? pending = null) : IOutboxStore
    {
        private int _claimed;

        public OutboxMessage? Enqueued { get; private set; }
        public TaskCompletionSource<Guid> Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(Guid MessageId, DateTimeOffset RetryAtUtc, string Error)> Failed { get; }
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
            Failed.TrySetResult((messageId, retryAtUtc, error));
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
