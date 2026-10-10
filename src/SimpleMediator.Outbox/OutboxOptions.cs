namespace SimpleMediator.Outbox;

/// <summary>Options for the hosted outbox dispatcher.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum number of messages claimed sequentially per polling cycle. Defaults to 32.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Delay between polls when no messages are available or a poll fails. Defaults to 5 seconds.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Lease duration for claimed messages. Defaults to 1 minute.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Delay before retrying a message whose publication failed. Defaults to 10 seconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);
}
