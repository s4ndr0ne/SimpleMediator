namespace SimpleMediator.Outbox;

/// <summary>A serialized notification waiting to be dispatched.</summary>
/// <param name="Id">Stable identifier assigned when the message is enqueued.</param>
/// <param name="Type">Application-defined notification type key.</param>
/// <param name="Payload">Serialized notification payload.</param>
/// <param name="OccurredAtUtc">UTC time at which the message was added to the outbox.</param>
public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAtUtc);
