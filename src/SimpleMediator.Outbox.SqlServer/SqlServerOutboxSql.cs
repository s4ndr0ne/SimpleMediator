namespace SimpleMediator.Outbox.SqlServer;

internal sealed class SqlServerOutboxSql
{
    /// <summary>Maximum length stored in <c>LastError</c>; longer error text is truncated by the store.</summary>
    internal const int MaxErrorLength = 4000;

    private const int MaxTableNameLength = 100;

    public string QualifiedTableName { get; }

    public string EnsureSchemaSql { get; }

    public string EnqueueSql { get; }

    public string ClaimBatchSql { get; }

    public string RenewLeaseSql { get; }

    public string MarkProcessedSql { get; }

    public string MarkFailedSql { get; }

    public string PurgeProcessedSql { get; }

    public string PurgeDeadLetteredSql { get; }

    public SqlServerOutboxSql(string schemaName, string tableName)
    {
        ValidateIdentifier(schemaName, nameof(schemaName));
        ValidateIdentifier(tableName, nameof(tableName));
        ValidateTableName(tableName);

        QualifiedTableName = $"[{schemaName}].[{tableName}]";
        var pendingIndexName = $"IX_{tableName}_Pending";
        var processedIndexName = $"IX_{tableName}_Processed";
        var deadLetterIndexName = $"IX_{tableName}_DeadLettered";
        var objectFullName = $"{schemaName}.{tableName}";
        var schemaLockResource = $"SimpleMediator.Outbox.Schema.{objectFullName}";

        EnsureSchemaSql = $"""
            DECLARE @LockResult INT;

            EXEC @LockResult = sys.sp_getapplock
                @Resource = N'{schemaLockResource}',
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = 30000;

            IF @LockResult < 0
            BEGIN
                THROW 50000, N'Could not acquire the outbox schema lock; another instance may be creating the schema.', 1;
            END;

            BEGIN TRY
                IF SCHEMA_ID(N'{schemaName}') IS NULL
                BEGIN
                    EXEC(N'CREATE SCHEMA [{schemaName}]');
                END;

                IF OBJECT_ID(N'{objectFullName}', N'U') IS NULL
                BEGIN
                    CREATE TABLE {QualifiedTableName}
                    (
                        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_{tableName}] PRIMARY KEY,
                        Type NVARCHAR(256) NOT NULL,
                        Payload NVARCHAR(MAX) NOT NULL,
                        OccurredAtUtc DATETIMEOFFSET(7) NOT NULL,
                        AvailableAtUtc DATETIMEOFFSET(7) NOT NULL,
                        Attempts INT NOT NULL CONSTRAINT [DF_{tableName}_Attempts] DEFAULT (0),
                        LastError NVARCHAR({MaxErrorLength}) NULL,
                        LeaseToken UNIQUEIDENTIFIER NULL,
                        LeaseExpiresAtUtc DATETIMEOFFSET(7) NULL,
                        ProcessedAtUtc DATETIMEOFFSET(7) NULL,
                        DeadLetteredAtUtc DATETIMEOFFSET(7) NULL
                    );
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{pendingIndexName}' AND object_id = OBJECT_ID(N'{objectFullName}'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [{pendingIndexName}]
                    ON {QualifiedTableName} (AvailableAtUtc ASC, OccurredAtUtc ASC)
                    INCLUDE (LeaseExpiresAtUtc)
                    WHERE ProcessedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL;
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{processedIndexName}' AND object_id = OBJECT_ID(N'{objectFullName}'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [{processedIndexName}]
                    ON {QualifiedTableName} (ProcessedAtUtc ASC)
                    WHERE ProcessedAtUtc IS NOT NULL;
                END;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{deadLetterIndexName}' AND object_id = OBJECT_ID(N'{objectFullName}'))
                BEGIN
                    CREATE NONCLUSTERED INDEX [{deadLetterIndexName}]
                    ON {QualifiedTableName} (DeadLetteredAtUtc ASC)
                    WHERE DeadLetteredAtUtc IS NOT NULL;
                END;
            END TRY
            BEGIN CATCH
                EXEC sys.sp_releaseapplock @Resource = N'{schemaLockResource}', @LockOwner = 'Session';
                THROW;
            END CATCH;

            EXEC sys.sp_releaseapplock @Resource = N'{schemaLockResource}', @LockOwner = 'Session';
            """;

        EnqueueSql = $"""
            INSERT INTO {QualifiedTableName} (Id, Type, Payload, OccurredAtUtc, AvailableAtUtc, Attempts)
            VALUES (@Id, @Type, @Payload, @OccurredAtUtc, @OccurredAtUtc, 0);
            """;

        ClaimBatchSql = $"""
            ;WITH Candidates AS
            (
                SELECT TOP (@BatchSize)
                    Id,
                    Type,
                    Payload,
                    OccurredAtUtc,
                    Attempts,
                    LeaseToken,
                    LeaseExpiresAtUtc
                FROM {QualifiedTableName} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAtUtc IS NULL
                  AND DeadLetteredAtUtc IS NULL
                  AND AvailableAtUtc <= @NowUtc
                  AND (LeaseExpiresAtUtc IS NULL OR LeaseExpiresAtUtc <= @NowUtc)
                ORDER BY AvailableAtUtc ASC, OccurredAtUtc ASC
            )
            UPDATE Candidates
            SET LeaseToken = NEWID(),
                LeaseExpiresAtUtc = @LeaseExpiresAtUtc
            OUTPUT
                inserted.Id,
                inserted.Type,
                inserted.Payload,
                inserted.OccurredAtUtc,
                inserted.LeaseToken,
                inserted.Attempts;
            """;

        RenewLeaseSql = $"""
            UPDATE {QualifiedTableName}
            SET LeaseExpiresAtUtc = @NewLeaseExpiresAtUtc
            WHERE Id = @Id
              AND LeaseToken = @LeaseToken
              AND ProcessedAtUtc IS NULL
              AND DeadLetteredAtUtc IS NULL
              AND LeaseExpiresAtUtc > @NowUtc;
            """;

        // Completion is intentionally allowed while the lease is still owned even if it has just
        // expired: the token fences out other workers, and letting the owner record success avoids
        // redelivering a message whose handler already ran.
        MarkProcessedSql = $"""
            UPDATE {QualifiedTableName}
            SET ProcessedAtUtc = @ProcessedAtUtc,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL
            WHERE Id = @Id
              AND LeaseToken = @LeaseToken
              AND ProcessedAtUtc IS NULL
              AND DeadLetteredAtUtc IS NULL;
            """;

        MarkFailedSql = $"""
            UPDATE {QualifiedTableName}
            SET Attempts = Attempts + 1,
                LastError = @Error,
                AvailableAtUtc = CASE WHEN Attempts + 1 >= @MaxAttempts THEN AvailableAtUtc ELSE @RetryAtUtc END,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                DeadLetteredAtUtc = CASE WHEN Attempts + 1 >= @MaxAttempts THEN @NowUtc ELSE NULL END
            WHERE Id = @Id
              AND LeaseToken = @LeaseToken
              AND ProcessedAtUtc IS NULL
              AND DeadLetteredAtUtc IS NULL;
            """;

        PurgeProcessedSql = $"""
            DELETE TOP (@BatchSize) FROM {QualifiedTableName}
            WHERE ProcessedAtUtc IS NOT NULL
              AND ProcessedAtUtc < @CutoffUtc;
            """;

        PurgeDeadLetteredSql = $"""
            DELETE TOP (@BatchSize) FROM {QualifiedTableName}
            WHERE DeadLetteredAtUtc IS NOT NULL
              AND DeadLetteredAtUtc < @CutoffUtc;
            """;
    }

    internal static bool IsValidIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128)
        {
            return false;
        }

        for (var i = 0; i < identifier.Length; i++)
        {
            var ch = identifier[i];
            var isValid = char.IsLetterOrDigit(ch) || ch == '_';
            if (!isValid || (i == 0 && char.IsDigit(ch)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Table names are also used to derive constraint and index names, which SQL Server limits to 128
    /// characters, so they get a tighter bound than a bare identifier.
    /// </summary>
    internal static bool IsValidTableName(string? tableName)
        => IsValidIdentifier(tableName) && tableName!.Length <= MaxTableNameLength;

    private static void ValidateTableName(string tableName)
    {
        if (!IsValidTableName(tableName))
        {
            throw new ArgumentException(
                $"'{tableName}' is not a valid SQL Server table name. Use letters, digits, and underscores only, with at most {MaxTableNameLength} characters.",
                nameof(tableName));
        }
    }

    private static void ValidateIdentifier(string identifier, string paramName)
    {
        if (!IsValidIdentifier(identifier))
        {
            throw new ArgumentException(
                $"'{identifier}' is not a valid SQL Server identifier. Use letters, digits, and underscores only.",
                paramName);
        }
    }
}
