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
}
