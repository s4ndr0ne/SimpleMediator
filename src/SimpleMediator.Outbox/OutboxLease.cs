namespace SimpleMediator.Outbox;

/// <summary>A message claim and the token required to renew or complete that specific claim.</summary>
/// <param name="Message">The claimed outbox message.</param>
/// <param name="LeaseToken">A unique token assigned to this claim.</param>
public sealed record OutboxLease(OutboxMessage Message, Guid LeaseToken)
{
    /// <summary>
    /// Number of delivery attempts the store had already recorded for this message when the lease was
    /// issued. Stores should populate it so the dispatcher can apply exponential backoff; it defaults
    /// to <c>0</c> for stores that do not track attempts.
    /// </summary>
    public int Attempts { get; init; }
}
