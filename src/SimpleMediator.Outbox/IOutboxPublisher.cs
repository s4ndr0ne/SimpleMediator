using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

/// <summary>Adds mediator notifications to the durable outbox instead of publishing them immediately.</summary>
public interface IOutboxPublisher
{
    /// <summary>Serializes and persists a notification in the current application transaction.</summary>
    Task EnqueueAsync<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification;
}
