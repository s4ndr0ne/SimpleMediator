using System.Collections.Frozen;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

/// <summary>AOT-safe <see cref="IOutboxSerializer"/> backed by <see cref="System.Text.Json"/> and explicit type keys.</summary>
public sealed class JsonOutboxSerializer : IOutboxSerializer
{
    private readonly FrozenDictionary<string, JsonOutboxSerializerBuilder.Entry> _byKey;
    private readonly FrozenDictionary<Type, JsonOutboxSerializerBuilder.Entry> _byType;

    internal JsonOutboxSerializer(
        FrozenDictionary<string, JsonOutboxSerializerBuilder.Entry> byKey,
        FrozenDictionary<Type, JsonOutboxSerializerBuilder.Entry> byType)
    {
        _byKey = byKey;
        _byType = byType;
    }

    /// <inheritdoc />
    public OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var notificationType = notification.GetType();
        if (!_byType.TryGetValue(notificationType, out var entry))
        {
            throw new InvalidOperationException(
                $"Notification type '{notificationType.FullName}' is not registered with {nameof(JsonOutboxSerializer)}.");
        }

        var payload = entry.Serialize(notification);
        return new OutboxMessage(messageId, entry.TypeKey, payload, occurredAtUtc);
    }

    /// <inheritdoc />
    public IOutboxDispatchable Deserialize(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_byKey.TryGetValue(message.Type, out var entry))
        {
            throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
        }

        return entry.Deserialize(message.Payload);
    }
}
