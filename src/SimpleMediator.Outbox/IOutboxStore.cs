namespace SimpleMediator.Outbox;

/// <summary>Application-provided storage for atomically enqueuing and leasing outbox messages.</summary>
/// <remarks>
/// <see cref="EnqueueAsync"/> must participate in the same database transaction as the application
/// changes whose notification it represents. Claims and lease updates must be atomic across competing
/// workers. A lease must expire so abandoned messages can be claimed again, and every claim must have
/// a new token so an expired owner cannot change a later owner's lease. Implementations define retry
/// limits and dead-letter policy.
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
    Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Extends an active lease if <paramref name="lease"/> still owns the message.</summary>
    /// <returns><see langword="true"/> if the lease was renewed; otherwise the lease was lost.</returns>
    Task<bool> RenewLeaseAsync(
        OutboxLease lease,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a successfully published message as complete if the lease token is current.</summary>
    /// <returns><see langword="true"/> if the message was marked processed; otherwise the lease was lost.</returns>
    Task<bool> MarkProcessedAsync(
        OutboxLease lease,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a failed message if the lease token is current.</summary>
    /// <remarks>The implementation should retain failure details and increment its attempt count.</remarks>
    /// <returns><see langword="true"/> if the message was released; otherwise the lease was lost.</returns>
    Task<bool> MarkFailedAsync(
        OutboxLease lease,
        DateTimeOffset retryAtUtc,
        string error,
        CancellationToken cancellationToken = default);
}
