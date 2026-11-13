using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox.Tests;

public sealed partial class JsonOutboxSerializerTests
{
    [Fact]
    public async Task Serialize_and_Deserialize_roundtrip_registered_notification()
    {
        var serializer = new JsonOutboxSerializerBuilder()
            .AddNotification("order.created.v1", TestJsonContext.Default.OrderCreatedNotification)
            .Build();

        var messageId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var original = new OrderCreatedNotification(42, "EUR");

        var message = serializer.Serialize(original, messageId, occurredAt);

        Assert.Equal(messageId, message.Id);
        Assert.Equal("order.created.v1", message.Type);
        Assert.Equal(occurredAt, message.OccurredAtUtc);

        var dispatchable = serializer.Deserialize(message);
        var typed = Assert.IsType<OutboxDispatchable<OrderCreatedNotification>>(dispatchable);
        Assert.Equal(original, typed.Notification);

        var publisher = new RecordingPublisher();
        await dispatchable.PublishAsync(publisher, CancellationToken.None);
        Assert.Equal([original], publisher.Published);
    }

    [Fact]
    public void Serialize_throws_for_unregistered_notification_type()
    {
        var serializer = new JsonOutboxSerializerBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            serializer.Serialize(
                new OrderCreatedNotification(1, "USD"),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));

        Assert.Contains("not registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_throws_for_unknown_type_key()
    {
        var serializer = new JsonOutboxSerializerBuilder()
            .AddNotification("order.created.v1", TestJsonContext.Default.OrderCreatedNotification)
            .Build();

        var message = new OutboxMessage(Guid.NewGuid(), "unknown.type", "{}", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(message));
        Assert.Contains("unknown.type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_duplicate_type_key_or_notification_type()
    {
        var builder = new JsonOutboxSerializerBuilder()
            .AddNotification("order.created.v1", TestJsonContext.Default.OrderCreatedNotification);

        Assert.Throws<ArgumentException>(() =>
            builder.AddNotification("order.created.v1", TestJsonContext.Default.InvoiceIssuedNotification));

        Assert.Throws<ArgumentException>(() =>
            builder.AddNotification("order.created.v2", TestJsonContext.Default.OrderCreatedNotification));
    }

    [Fact]
    public void AddJsonOutboxSerializer_registers_singleton_IOutboxSerializer()
    {
        var services = new ServiceCollection();
        services.AddJsonOutboxSerializer(builder =>
            builder.AddNotification("order.created.v1", TestJsonContext.Default.OrderCreatedNotification));

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IOutboxSerializer>();

        Assert.IsType<JsonOutboxSerializer>(serializer);
    }

    private sealed record OrderCreatedNotification(int OrderId, string Currency) : INotification;

    private sealed record InvoiceIssuedNotification(string InvoiceNumber) : INotification;

    private sealed class RecordingPublisher : IPublisher
    {
        public List<INotification> Published { get; } = [];

        public Task Publish(object notification, CancellationToken cancellationToken = default)
        {
            Published.Add((INotification)notification);
            return Task.CompletedTask;
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Published.Add(notification);
            return Task.CompletedTask;
        }
    }

    [JsonSerializable(typeof(OrderCreatedNotification))]
    [JsonSerializable(typeof(InvoiceIssuedNotification))]
    private sealed partial class TestJsonContext : JsonSerializerContext;
}
