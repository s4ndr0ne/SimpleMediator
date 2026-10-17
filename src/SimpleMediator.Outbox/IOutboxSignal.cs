namespace SimpleMediator.Outbox;

/// <summary>
/// Signals the hosted outbox dispatcher that one or more messages may be ready to claim,
/// waking it from its polling delay immediately.
/// </summary>
public interface IOutboxSignal
{
    /// <summary>
    /// Signals that new outbox messages are available for dispatch. This method is thread-safe
    /// and never blocks.
    /// </summary>
    void Pulse();

    /// <summary>
    /// Waits until <see cref="Pulse"/> is called or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="timeout">Maximum time to wait when no signal arrives.</param>
    /// <param name="timeProvider">Time provider used for the timeout delay.</param>
    /// <param name="cancellationToken">Token that cancels the wait.</param>
    /// <returns><see langword="true"/> if awakened by <see cref="Pulse"/>; <see langword="false"/> if the timeout elapsed.</returns>
    Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default);
}
