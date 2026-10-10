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

    /// <summary>Delay before retrying a message whose publication failed. Defaults to 10 seconds.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);
}
