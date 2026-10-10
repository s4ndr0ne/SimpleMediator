namespace SimpleMediator.Outbox;

/// <summary>A message claim and the token required to renew or complete that specific claim.</summary>
/// <param name="Message">The claimed outbox message.</param>
/// <param name="LeaseToken">A unique token assigned to this claim.</param>
public sealed record OutboxLease(OutboxMessage Message, Guid LeaseToken);
