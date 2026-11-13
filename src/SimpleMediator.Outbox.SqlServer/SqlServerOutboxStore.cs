using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace SimpleMediator.Outbox.SqlServer;

/// <summary>SQL Server <see cref="IOutboxStore"/> implementation backed by <see cref="Microsoft.Data.SqlClient"/>.</summary>
public sealed class SqlServerOutboxStore : IOutboxStore
{
    private const string AmbientTransactionRequiredMessage =
        "The outbox message must be enqueued inside the application transaction. Call " +
        "ISqlServerOutboxSession.UseTransaction(connection, transaction) (or BeginScope) before enqueuing, " +
        "or set SqlServerOutboxOptions.EnqueueRequiresTransaction = false to allow standalone writes.";

    private readonly SqlServerOutboxOptions _options;
    private readonly ISqlServerOutboxSession? _session;
    private readonly TimeProvider _timeProvider;
    private readonly SqlServerOutboxSql _sql;

    /// <summary>Initializes a new <see cref="SqlServerOutboxStore"/>.</summary>
    public SqlServerOutboxStore(
        IOptions<SqlServerOutboxOptions> options,
        ISqlServerOutboxSession? session = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _session = session;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sql = new SqlServerOutboxSql(_options.SchemaName, _options.TableName);
    }

    /// <summary>Creates the outbox table and its indexes if they do not already exist.</summary>
    /// <remarks>
    /// Schema creation is serialized across application instances with a SQL Server application lock, so a
    /// rolling deployment cannot race two <c>CREATE</c> statements. Prefer a migration for production
    /// deployments and use this only for development or self-provisioning services.
    /// </remarks>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateCommand(connection, null, _sql.EnsureSchemaSql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// No ambient transaction is bound and <see cref="SqlServerOutboxOptions.EnqueueRequiresTransaction"/> is
    /// <see langword="true"/>.
    /// </exception>
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_session?.Connection is { } ambientConnection)
        {
            var transaction = _session.Transaction;
            if (transaction is not null && !ReferenceEquals(transaction.Connection, ambientConnection))
            {
                throw new InvalidOperationException(
                    "The ambient outbox transaction belongs to a different connection than the bound session connection.");
            }

            if (transaction is null && _options.EnqueueRequiresTransaction)
            {
                throw new InvalidOperationException(AmbientTransactionRequiredMessage);
            }

            if (ambientConnection.State != ConnectionState.Open)
            {
                await ambientConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            using var ambientCommand = CreateEnqueueCommand(ambientConnection, transaction, message);
            await ambientCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_options.EnqueueRequiresTransaction)
        {
            throw new InvalidOperationException(AmbientTransactionRequiredMessage);
        }

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateEnqueueCommand(connection, null, message);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);
        var leases = new List<OutboxLease>(Math.Min(batchSize, 64));

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateCommand(connection, null, _sql.ClaimBatchSql);
            command.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTimeOffset) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@LeaseExpiresAtUtc", SqlDbType.DateTimeOffset) { Value = leaseExpiresAtUtc });

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var message = new OutboxMessage(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetDateTimeOffset(3));

                    var leaseToken = reader.GetGuid(4);
                    var attempts = reader.GetInt32(5);
                    leases.Add(new OutboxLease(message, leaseToken) { Attempts = attempts });
                }
            }
        }

        return leases;
    }

    /// <inheritdoc />
    public async Task<bool> RenewLeaseAsync(
        OutboxLease lease,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateCommand(connection, null, _sql.RenewLeaseSql);
            command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = lease.Message.Id });
            command.Parameters.Add(new SqlParameter("@LeaseToken", SqlDbType.UniqueIdentifier) { Value = lease.LeaseToken });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTimeOffset) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@NewLeaseExpiresAtUtc", SqlDbType.DateTimeOffset) { Value = nowUtc.Add(leaseDuration) });

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkProcessedAsync(
        OutboxLease lease,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateCommand(connection, null, _sql.MarkProcessedSql);
            command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = lease.Message.Id });
            command.Parameters.Add(new SqlParameter("@LeaseToken", SqlDbType.UniqueIdentifier) { Value = lease.LeaseToken });
            command.Parameters.Add(new SqlParameter("@ProcessedAtUtc", SqlDbType.DateTimeOffset) { Value = processedAtUtc });

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        OutboxLease lease,
        DateTimeOffset retryAtUtc,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(error);

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            using var command = CreateCommand(connection, null, _sql.MarkFailedSql);
            command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = lease.Message.Id });
            command.Parameters.Add(new SqlParameter("@LeaseToken", SqlDbType.UniqueIdentifier) { Value = lease.LeaseToken });
            command.Parameters.Add(new SqlParameter("@Error", SqlDbType.NVarChar, SqlServerOutboxSql.MaxErrorLength) { Value = TruncateError(error) });
            command.Parameters.Add(new SqlParameter("@RetryAtUtc", SqlDbType.DateTimeOffset) { Value = retryAtUtc });
            command.Parameters.Add(new SqlParameter("@MaxAttempts", SqlDbType.Int) { Value = _options.MaxAttempts });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTimeOffset) { Value = _timeProvider.GetUtcNow() });

            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return affected > 0;
        }
    }

    /// <summary>
    /// Deletes processed and dead-lettered messages older than their configured retention windows.
    /// Deletion runs in batches bounded by <see cref="SqlServerOutboxOptions.PurgeBatchSize"/> and
    /// <see cref="SqlServerOutboxOptions.MaxPurgeBatchesPerCycle"/>.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var purged = 0;

        if (_options.ProcessedMessageRetention is { } processedRetention)
        {
            purged += await PurgeCategoryAsync(
                _sql.PurgeProcessedSql,
                nowUtc - processedRetention,
                cancellationToken).ConfigureAwait(false);
        }

        if (_options.DeadLetterRetention is { } deadLetterRetention)
        {
            purged += await PurgeCategoryAsync(
                _sql.PurgeDeadLetteredSql,
                nowUtc - deadLetterRetention,
                cancellationToken).ConfigureAwait(false);
        }

        return purged;
    }

    private async Task<int> PurgeCategoryAsync(string sql, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;
        for (var batch = 0; batch < _options.MaxPurgeBatchesPerCycle; batch++)
        {
            var affected = 0;
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                using var command = CreateCommand(connection, null, sql);
                command.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = _options.PurgeBatchSize });
                command.Parameters.Add(new SqlParameter("@CutoffUtc", SqlDbType.DateTimeOffset) { Value = cutoffUtc });
                affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            deleted += affected;
            if (affected < _options.PurgeBatchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqlCommand CreateEnqueueCommand(SqlConnection connection, SqlTransaction? transaction, OutboxMessage message)
    {
        var command = CreateCommand(connection, transaction, _sql.EnqueueSql);
        command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = message.Id });
        command.Parameters.Add(new SqlParameter("@Type", SqlDbType.NVarChar, 256) { Value = message.Type });
        command.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = message.Payload });
        command.Parameters.Add(new SqlParameter("@OccurredAtUtc", SqlDbType.DateTimeOffset) { Value = message.OccurredAtUtc });
        return command;
    }

    private static string TruncateError(string error)
    {
        if (error.Length <= SqlServerOutboxSql.MaxErrorLength)
        {
            return error;
        }

        var length = char.IsHighSurrogate(error[SqlServerOutboxSql.MaxErrorLength - 1])
            ? SqlServerOutboxSql.MaxErrorLength - 1
            : SqlServerOutboxSql.MaxErrorLength;
        return error[..length];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL statements only interpolate validated SQL identifiers from SqlServerOutboxSql.")]
    private SqlCommand CreateCommand(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.Text;
        command.CommandText = sql;
        command.CommandTimeout = (int)Math.Ceiling(_options.CommandTimeout.TotalSeconds);
        return command;
    }
}
