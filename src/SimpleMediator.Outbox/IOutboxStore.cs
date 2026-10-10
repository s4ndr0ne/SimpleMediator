namespace SimpleMediator.Outbox;

/// <summary>Application-provided storage for atomically enqueuing and leasing outbox messages.</summary>
/// <remarks>
/// <see cref="EnqueueAsync"/> must participate in the same database transaction as the application
/// changes whose notification it represents. <see cref="ClaimBatchAsync"/> must atomically lease
/// returned rows across competing workers. A lease must expire so abandoned messages can be claimed
/// again. Implementations define retry limits and dead-letter policy.
/// </remarks>
public interface IOutboxStore
{
    /// <summary>Persists a message in the current application transaction.</summary>
    Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>Atomically leases a batch of messages that are ready for delivery.</summary>
    /// <param name="batchSize">Maximum number of messages to claim.</param>
    /// <param name="nowUtc">Current UTC time used to select eligible messages.</param>
    /// <param name="leaseDuration">How long the claim remains valid if the worker stops.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a successfully published message as complete.</summary>
    Task MarkProcessedAsync(
        Guid messageId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a failed message and makes it eligible again after <paramref name="retryAtUtc"/>.</summary>
    /// <remarks>The implementation should retain the failure details and increment its attempt count.</remarks>
    Task MarkFailedAsync(
        Guid messageId,
        DateTimeOffset retryAtUtc,
        string error,
        CancellationToken cancellationToken = default);
}
