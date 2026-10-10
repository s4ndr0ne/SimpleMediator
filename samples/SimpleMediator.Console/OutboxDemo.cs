using System.Text.Json;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;

namespace SimpleMediator.ConsoleSample;

public sealed record CreateOrderRequest(string Name) : IRequest<Guid>;

public sealed record OrderCreatedNotification(Guid OrderId, string Name) : INotification;

public sealed class CreateOrderHandler(IOutboxPublisher outboxPublisher)
    : IRequestHandler<CreateOrderRequest, Guid>
{
    public async Task<Guid> Handle(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();
        await outboxPublisher.EnqueueAsync(
            new OrderCreatedNotification(orderId, request.Name),
            cancellationToken);
        return orderId;
    }
}

public sealed class OrderCreatedHandler(OutboxDemoSignal signal)
    : INotificationHandler<OrderCreatedNotification>
{
    public Task Handle(OrderCreatedNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Outbox delivered: order {notification.OrderId} for {notification.Name}");
        signal.MarkDelivered();
        return Task.CompletedTask;
    }
}

public sealed class OutboxDemoSignal
{
    private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Delivered => _delivered.Task;

    public void MarkDelivered() => _delivered.TrySetResult();
}

public sealed class SampleOutboxSerializer : IOutboxSerializer
{
    private const string OrderCreatedType = "order-created-v1";

    public OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc)
    {
        if (notification is not OrderCreatedNotification orderCreated)
        {
            throw new NotSupportedException($"Notification type '{notification.GetType().FullName}' is not configured for the sample outbox.");
        }

        return new OutboxMessage(
            messageId,
            OrderCreatedType,
            JsonSerializer.Serialize(orderCreated),
            occurredAtUtc);
    }

    public IOutboxDispatchable Deserialize(OutboxMessage message)
    {
        if (message.Type != OrderCreatedType)
        {
            throw new NotSupportedException($"Outbox message type '{message.Type}' is not configured for the sample.");
        }

        var notification = JsonSerializer.Deserialize<OrderCreatedNotification>(message.Payload)
            ?? throw new InvalidOperationException("The order-created payload was empty.");
        return new OutboxDispatchable<OrderCreatedNotification>(notification);
    }
}

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _entries.Add(message.Id, new Entry(message));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var entries = _entries.Values
                .Where(entry => entry.ProcessedAtUtc is null
                    && entry.AvailableAtUtc <= nowUtc
                    && (entry.LeaseUntilUtc is null || entry.LeaseUntilUtc <= nowUtc))
                .OrderBy(entry => entry.Message.OccurredAtUtc)
                .Take(batchSize)
                .ToArray();

            foreach (var entry in entries)
            {
                entry.LeaseUntilUtc = nowUtc.Add(leaseDuration);
            }

            IReadOnlyList<OutboxMessage> messages = entries.Select(entry => entry.Message).ToArray();
            return Task.FromResult(messages);
        }
    }

    public Task MarkProcessedAsync(Guid messageId, DateTimeOffset processedAtUtc, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var entry = GetEntry(messageId);
            entry.ProcessedAtUtc = processedAtUtc;
            entry.LeaseUntilUtc = null;
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid messageId, DateTimeOffset retryAtUtc, string error, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var entry = GetEntry(messageId);
            entry.AvailableAtUtc = retryAtUtc;
            entry.LastError = error;
            entry.LeaseUntilUtc = null;
        }

        return Task.CompletedTask;
    }

    private Entry GetEntry(Guid messageId)
        => _entries.TryGetValue(messageId, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Outbox message '{messageId}' was not found.");

    private sealed class Entry(OutboxMessage message)
    {
        public OutboxMessage Message { get; } = message;
        public DateTimeOffset AvailableAtUtc { get; set; } = message.OccurredAtUtc;
        public DateTimeOffset? LeaseUntilUtc { get; set; }
        public DateTimeOffset? ProcessedAtUtc { get; set; }
        public string? LastError { get; set; }
    }
}
