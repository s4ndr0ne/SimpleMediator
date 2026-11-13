namespace SimpleMediator.Outbox.SqlServer;

/// <summary>Configuration options for <see cref="SqlServerOutboxStore"/>.</summary>
public sealed class SqlServerOutboxOptions
{
    /// <summary>SQL Server connection string used when no active <see cref="ISqlServerOutboxSession"/> transaction is bound.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Database schema containing the outbox table. Defaults to <c>dbo</c>.</summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>Outbox table name. Defaults to <c>OutboxMessages</c>.</summary>
    public string TableName { get; set; } = "OutboxMessages";

    /// <summary>Maximum delivery attempts before a message is marked dead-lettered. Defaults to 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Command timeout applied to SQL Server outbox operations. Defaults to 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When <see langword="true"/>, ensures the outbox table and index exist on host startup. Defaults to <see langword="false"/>.</summary>
    public bool EnsureSchemaOnStartup { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default), <see cref="SqlServerOutboxStore.EnqueueAsync"/> throws unless
    /// <see cref="ISqlServerOutboxSession"/> has an active transaction bound, so an outbox message can never be
    /// written outside the application transaction by accident. Set to <see langword="false"/> only for
    /// non-transactional writers.
    /// </summary>
    public bool EnqueueRequiresTransaction { get; set; } = true;

    /// <summary>
    /// How long processed messages are kept before the purge worker deletes them. Defaults to 7 days.
    /// Set to <see langword="null"/> to disable purging of processed messages.
    /// </summary>
    public TimeSpan? ProcessedMessageRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long dead-lettered messages are kept before the purge worker deletes them. Defaults to 30 days.
    /// Set to <see langword="null"/> to disable purging of dead-lettered messages.
    /// </summary>
    public TimeSpan? DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Interval between purge cycles. Defaults to 1 hour.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum rows deleted per purge statement. Defaults to 1000.</summary>
    public int PurgeBatchSize { get; set; } = 1000;

    /// <summary>
    /// Maximum number of purge batches executed per category per cycle, which bounds how long a single
    /// cycle can run against a large backlog. Defaults to 20.
    /// </summary>
    public int MaxPurgeBatchesPerCycle { get; set; } = 20;
}
