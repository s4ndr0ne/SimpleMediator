using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

/// <summary>Converts notifications to and from application-defined outbox payloads.</summary>
/// <remarks>
/// Use stable type keys rather than assembly-qualified CLR names so persisted messages survive
/// application refactoring. Implementations should reject unknown or invalid message types.
/// </remarks>
public interface IOutboxSerializer
{
    /// <summary>Serializes a notification to a persisted outbox message.</summary>
    OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc);

    /// <summary>Deserializes a persisted message into an object that can publish the typed notification.</summary>
    IOutboxDispatchable Deserialize(OutboxMessage message);
}

/// <summary>A deserialized notification that can be dispatched without runtime reflection.</summary>
public interface IOutboxDispatchable
{
    /// <summary>Publishes the notification using the mediator publisher.</summary>
    Task PublishAsync(IPublisher publisher, CancellationToken cancellationToken = default);
}

/// <summary>A strongly typed dispatchable notification for an outbox serializer to return.</summary>
/// <typeparam name="TNotification">The notification type.</typeparam>
public sealed class OutboxDispatchable<TNotification>(TNotification notification) : IOutboxDispatchable
    where TNotification : INotification
{
    /// <summary>The deserialized notification.</summary>
    public TNotification Notification { get; } = notification;

    /// <inheritdoc />
    public Task PublishAsync(IPublisher publisher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        return publisher.Publish(Notification, cancellationToken);
    }
}
