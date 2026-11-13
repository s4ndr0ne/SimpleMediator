namespace SimpleMediator.Outbox.SqlServer;

internal sealed class SqlServerOutboxSql
{
    public string QualifiedTableName { get; }

    public string EnsureSchemaSql { get; }

    public string EnqueueSql { get; }

    public string ClaimBatchSql { get; }

    public string RenewLeaseSql { get; }

    public string MarkProcessedSql { get; }

    public string MarkFailedSql { get; }

    public SqlServerOutboxSql(string schemaName, string tableName)
    {
        ValidateIdentifier(schemaName, nameof(schemaName));
        ValidateIdentifier(tableName, nameof(tableName));

        QualifiedTableName = $"[{schemaName}].[{tableName}]";
        var indexName = $"IX_{tableName}_Pending";
        var objectFullName = $"{schemaName}.{tableName}";

        EnsureSchemaSql = $"""
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
                    LastError NVARCHAR(MAX) NULL,
                    LeaseToken UNIQUEIDENTIFIER NULL,
                    LeaseExpiresAtUtc DATETIMEOFFSET(7) NULL,
                    ProcessedAtUtc DATETIMEOFFSET(7) NULL,
                    DeadLetteredAtUtc DATETIMEOFFSET(7) NULL
                );

                CREATE NONCLUSTERED INDEX [{indexName}]
                ON {QualifiedTableName} (AvailableAtUtc ASC, OccurredAtUtc ASC)
                INCLUDE (LeaseExpiresAtUtc)
                WHERE ProcessedAtUtc IS NULL AND DeadLetteredAtUtc IS NULL;
            END;
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
                inserted.LeaseToken;
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
                AvailableAtUtc = @RetryAtUtc,
                LeaseToken = NULL,
                LeaseExpiresAtUtc = NULL,
                DeadLetteredAtUtc = CASE WHEN Attempts + 1 >= @MaxAttempts THEN @NowUtc ELSE NULL END
            WHERE Id = @Id
              AND LeaseToken = @LeaseToken
              AND ProcessedAtUtc IS NULL
              AND DeadLetteredAtUtc IS NULL;
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
