namespace SimpleMediator.Outbox;

/// <summary>Options for the hosted outbox dispatcher.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum messages dispatched per polling cycle. Each is claimed immediately before
    /// dispatch. Defaults to 32.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Delay between polls when no messages are available or a poll fails. Defaults to 5 seconds.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Lease duration for claimed messages. Defaults to 1 minute.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Interval between lease renewals while a message is being dispatched. Defaults to 20 seconds.</summary>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Base delay before retrying a message whose publication failed. Defaults to 10 seconds.
    /// The delay actually applied grows exponentially per attempt; see
    /// <see cref="RetryBackoffMultiplier"/> and <see cref="MaxRetryDelay"/>.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Multiplier applied to <see cref="RetryDelay"/> for every failed attempt (1 means a fixed delay).
    /// Defaults to 2, so retries wait 10s, 20s, 40s, and so on. Must be at least 1.
    /// </summary>
    public double RetryBackoffMultiplier { get; set; } = 2;

    /// <summary>
    /// Upper bound for the computed retry delay. Defaults to 5 minutes. Values lower than
    /// <see cref="RetryDelay"/> are raised to <see cref="RetryDelay"/>, so the base delay is always honored.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fraction of the computed retry delay applied as symmetric jitter, in the range 0 to 1.
    /// Defaults to 0.2 (±20%). Jitter is derived deterministically from the lease token, so it spreads
    /// retries across workers without making tests flaky.
    /// </summary>
    public double RetryJitterFactor { get; set; } = 0.2;

    /// <summary>
    /// When <see langword="false"/> (the default), the worker claims one message immediately before dispatching it,
    /// up to <see cref="BatchSize"/> per cycle. When <see langword="true"/>, the worker claims up to <see cref="BatchSize"/>
    /// messages in a single store call per cycle to minimize database round-trips.
    /// </summary>
    public bool ClaimFullBatch { get; set; }

    /// <summary>
    /// Maximum number of claimed messages dispatched concurrently within a batch when <see cref="ClaimFullBatch"/>
    /// is enabled (or across concurrent claim slots). Defaults to 1 (sequential).
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;
}
