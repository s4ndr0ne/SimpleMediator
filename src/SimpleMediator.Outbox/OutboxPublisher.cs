using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's dependency-injection registration.")]
internal sealed class OutboxPublisher(IOutboxStore store, IOutboxSerializer serializer) : IOutboxPublisher
{
    public Task EnqueueAsync<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        var message = serializer.Serialize(notification, Guid.NewGuid(), DateTimeOffset.UtcNow);
        ArgumentNullException.ThrowIfNull(message);
        return store.EnqueueAsync(message, cancellationToken);
    }
}
