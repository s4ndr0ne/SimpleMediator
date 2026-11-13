using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;

namespace SimpleMediator.Outbox.SqlServer.Tests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver-outbox";
}

/// <summary>
/// Starts one SQL Server container for the whole test run and creates an isolated database. When Docker
/// is unavailable the tests using this fixture are reported as skipped instead of failing.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Password = "SimpleMediator_Test1!";

    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string SkipReason { get; private set; } = "SQL Server test container is unavailable.";

    public bool Available => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword(Password)
                .Build();

            await _container.StartAsync();

            var masterConnectionString = _container.GetConnectionString();
            await using (var connection = new SqlConnection(masterConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "IF DB_ID(N'OutboxTests') IS NULL CREATE DATABASE [OutboxTests];";
                await command.ExecuteNonQueryAsync();
            }

            ConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = "OutboxTests",
            }.ConnectionString;
        }
        catch (Exception exception)
        {
            ConnectionString = null;
            SkipReason = $"SQL Server test container unavailable: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerOutboxIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Enqueue_in_transaction_is_visible_only_after_commit()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        await CreateStore(table).EnsureSchemaAsync();

        var session = new SqlServerOutboxSession();
        var store = CreateStore(table, session: session);
        var message = NewMessage();

        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            using (session.BeginScope(connection, transaction))
            {
                await store.EnqueueAsync(message);
                Assert.Equal(1, await CountRowsAsync(table, connection, transaction));
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(1, await CountRowsAsync(table));
    }

    [SkippableFact]
    public async Task Enqueue_in_rolled_back_transaction_persists_nothing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        await CreateStore(table).EnsureSchemaAsync();

        var session = new SqlServerOutboxSession();
        var store = CreateStore(table, session: session);

        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

            using (session.BeginScope(connection, transaction))
            {
                await store.EnqueueAsync(NewMessage());
            }

            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await CountRowsAsync(table));
    }

    [SkippableFact]
    public async Task Enqueue_without_transaction_fails_closed_unless_opted_out()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var failClosedStore = CreateStore(table);
        await failClosedStore.EnsureSchemaAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failClosedStore.EnqueueAsync(NewMessage()));
        Assert.Contains("EnqueueRequiresTransaction", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountRowsAsync(table));

        var optedOutStore = CreateStore(table, enqueueRequiresTransaction: false);
        await optedOutStore.EnqueueAsync(NewMessage());
        Assert.Equal(1, await CountRowsAsync(table));
    }

    [SkippableFact]
    public async Task Enqueue_with_bound_connection_but_no_transaction_fails_closed()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        await CreateStore(table).EnsureSchemaAsync();

        var session = new SqlServerOutboxSession();
        var store = CreateStore(table, session: session);

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using (session.BeginScope(connection))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueAsync(NewMessage()));
        }

        Assert.Equal(0, await CountRowsAsync(table));
    }

    [SkippableFact]
    public async Task Concurrent_claims_never_return_the_same_message()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        const int messageCount = 40;
        var table = NewTableName();
        var store = CreateStore(table, enqueueRequiresTransaction: false);
        await store.EnsureSchemaAsync();

        for (var i = 0; i < messageCount; i++)
        {
            await store.EnqueueAsync(NewMessage());
        }

        var claimed = new List<OutboxLease>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 8),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (_, _) =>
            {
                for (var round = 0; round < 30; round++)
                {
                    var leases = await store.ClaimBatchAsync(10, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
                    lock (claimed)
                    {
                        claimed.AddRange(leases);
                    }

                    if (leases.Count > 0)
                    {
                        return;
                    }

                    await Task.Delay(10);
                }
            });

        Assert.Equal(messageCount, claimed.Count);
        Assert.Equal(messageCount, claimed.Select(lease => lease.Message.Id).Distinct().Count());
        Assert.Equal(messageCount, claimed.Select(lease => lease.LeaseToken).Distinct().Count());
    }

    [SkippableFact]
    public async Task Expired_lease_can_be_reclaimed_and_the_old_token_cannot_complete()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var store = CreateStore(table, enqueueRequiresTransaction: false);
        await store.EnsureSchemaAsync();
        await store.EnqueueAsync(NewMessage());

        var now = DateTimeOffset.UtcNow;
        var first = Assert.Single(await store.ClaimBatchAsync(1, now, TimeSpan.FromMilliseconds(100)));

        Assert.True(await store.RenewLeaseAsync(first, now.AddMilliseconds(50), TimeSpan.FromMilliseconds(100)));
        Assert.Empty(await store.ClaimBatchAsync(1, now.AddMilliseconds(120), TimeSpan.FromMilliseconds(100)));

        var reclaimed = Assert.Single(await store.ClaimBatchAsync(1, now.AddSeconds(5), TimeSpan.FromMilliseconds(100)));
        Assert.Equal(first.Message.Id, reclaimed.Message.Id);
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);

        Assert.False(await store.MarkProcessedAsync(first, now.AddSeconds(5)));
        Assert.True(await store.MarkProcessedAsync(reclaimed, now.AddSeconds(5)));
    }

    [SkippableFact]
    public async Task Failed_attempts_are_counted_and_the_message_is_dead_lettered()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var store = CreateStore(table, maxAttempts: 2, timeProvider: timeProvider, enqueueRequiresTransaction: false);
        await store.EnsureSchemaAsync();

        var message = NewMessage();
        await store.EnqueueAsync(message);

        var first = Assert.Single(await store.ClaimBatchAsync(10, timeProvider.Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, first.Attempts);
        var firstRetryAtUtc = timeProvider.Now.AddSeconds(1);
        Assert.True(await store.MarkFailedAsync(first, firstRetryAtUtc, "first failure"));

        timeProvider.Now = timeProvider.Now.AddSeconds(5);
        var second = Assert.Single(await store.ClaimBatchAsync(10, timeProvider.Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(1, second.Attempts);
        Assert.True(await store.MarkFailedAsync(second, timeProvider.Now.AddSeconds(1), "second failure"));

        timeProvider.Now = timeProvider.Now.AddMinutes(10);
        Assert.Empty(await store.ClaimBatchAsync(10, timeProvider.Now, TimeSpan.FromMinutes(1)));

        var row = Assert.Single(await QueryRowsAsync(table));
        Assert.Equal(2, row.Attempts);
        Assert.NotNull(row.DeadLetteredAtUtc);
        Assert.Null(row.ProcessedAtUtc);
        // Dead-lettering keeps the retry time from the last failed attempt instead of moving it to the
        // dead-letter timestamp.
        Assert.Equal(firstRetryAtUtc.UtcDateTime, row.AvailableAtUtc.UtcDateTime);
        Assert.Contains("second failure", row.LastError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Purge_deletes_only_rows_past_their_retention_window()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var store = CreateStore(
            table,
            maxAttempts: 1,
            timeProvider: timeProvider,
            enqueueRequiresTransaction: false,
            processedRetention: TimeSpan.FromHours(1),
            deadLetterRetention: TimeSpan.FromHours(1));
        await store.EnsureSchemaAsync();

        var processedMessage = NewMessage();
        await store.EnqueueAsync(processedMessage);
        var processedLease = Assert.Single(await store.ClaimBatchAsync(10, timeProvider.Now, TimeSpan.FromMinutes(1)));
        Assert.True(await store.MarkProcessedAsync(processedLease, timeProvider.Now.AddHours(-2)));

        var deadLetteredMessage = NewMessage();
        await store.EnqueueAsync(deadLetteredMessage);
        var deadLetteredLease = Assert.Single(await store.ClaimBatchAsync(10, timeProvider.Now, TimeSpan.FromMinutes(1)));
        Assert.True(await store.MarkFailedAsync(deadLetteredLease, timeProvider.Now, "poison"));

        await store.EnqueueAsync(NewMessage());

        timeProvider.Now = timeProvider.Now.AddHours(2);
        var purged = await store.PurgeAsync();

        Assert.Equal(2, purged);
        var remaining = Assert.Single(await QueryRowsAsync(table));
        Assert.Null(remaining.ProcessedAtUtc);
        Assert.Null(remaining.DeadLetteredAtUtc);
    }

    [SkippableFact]
    public async Task Concurrent_schema_creation_is_serialized()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var stores = Enumerable.Range(0, 6).Select(_ => CreateStore(table)).ToArray();

        await Task.WhenAll(stores.Select(store => store.EnsureSchemaAsync()));

        Assert.Equal(0, await CountRowsAsync(table));
        Assert.True(await IndexExistsAsync(table, $"IX_{table}_Pending"));
        Assert.True(await IndexExistsAsync(table, $"IX_{table}_Processed"));
        Assert.True(await IndexExistsAsync(table, $"IX_{table}_DeadLettered"));
    }

    [SkippableFact]
    public async Task Long_errors_are_truncated_to_the_column_size()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var table = NewTableName();
        var store = CreateStore(table, enqueueRequiresTransaction: false);
        await store.EnsureSchemaAsync();
        await store.EnqueueAsync(NewMessage());

        var lease = Assert.Single(await store.ClaimBatchAsync(10, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.True(await store.MarkFailedAsync(lease, DateTimeOffset.UtcNow, new string('x', SqlServerOutboxSql.MaxErrorLength + 500)));

        var row = Assert.Single(await QueryRowsAsync(table));
        Assert.Equal(SqlServerOutboxSql.MaxErrorLength, row.LastError!.Length);
    }

    private SqlServerOutboxStore CreateStore(
        string table,
        ISqlServerOutboxSession? session = null,
        TimeProvider? timeProvider = null,
        bool enqueueRequiresTransaction = true,
        int maxAttempts = 5,
        TimeSpan? processedRetention = null,
        TimeSpan? deadLetterRetention = null)
    {
        var options = new SqlServerOutboxOptions
        {
            ConnectionString = fixture.ConnectionString!,
            TableName = table,
            EnqueueRequiresTransaction = enqueueRequiresTransaction,
            MaxAttempts = maxAttempts,
            ProcessedMessageRetention = processedRetention,
            DeadLetterRetention = deadLetterRetention,
        };

        return new SqlServerOutboxStore(Options.Create(options), session, timeProvider);
    }

    private static string NewTableName() => $"Outbox_{Guid.NewGuid():N}"[..24];

    private static OutboxMessage NewMessage()
        => new(Guid.NewGuid(), "test.notification", "{}", DateTimeOffset.UtcNow);

    private async Task<int> CountRowsAsync(string table)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return await CountRowsAsync(table, connection, transaction: null);
    }

    private static async Task<int> CountRowsAsync(string table, SqlConnection connection, SqlTransaction? transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}];";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> IndexExistsAsync(string table, string indexName)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = @Name AND object_id = OBJECT_ID(@Table);";
        command.Parameters.AddWithValue("@Name", indexName);
        command.Parameters.AddWithValue("@Table", $"dbo.{table}");
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private async Task<List<OutboxRow>> QueryRowsAsync(string table)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Attempts, LastError, AvailableAtUtc, ProcessedAtUtc, DeadLetteredAtUtc
            FROM [dbo].[{table}]
            ORDER BY OccurredAtUtc;
            """;

        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetDateTimeOffset(2),
                reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
                reader.IsDBNull(4) ? null : reader.GetDateTimeOffset(4)));
        }

        return rows;
    }

    private sealed record OutboxRow(
        int Attempts,
        string? LastError,
        DateTimeOffset AvailableAtUtc,
        DateTimeOffset? ProcessedAtUtc,
        DateTimeOffset? DeadLetteredAtUtc);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
