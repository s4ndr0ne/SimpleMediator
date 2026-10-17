using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's dependency-injection registration.")]
internal sealed class OutboxPublisher(
    IOutboxStore store,
    IOutboxSerializer serializer,
    TimeProvider? timeProvider = null,
    IOutboxSignal? signal = null) : IOutboxPublisher
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task EnqueueAsync<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        var message = serializer.Serialize(notification, Guid.NewGuid(), _timeProvider.GetUtcNow());
        ArgumentNullException.ThrowIfNull(message);
        await store.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
        signal?.Pulse();
    }
}
