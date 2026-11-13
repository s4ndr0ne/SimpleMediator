using Microsoft.Data.SqlClient;

namespace SimpleMediator.Outbox.SqlServer;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's dependency-injection registration.")]
internal sealed class SqlServerOutboxSession : ISqlServerOutboxSession
{
    public SqlConnection? Connection { get; private set; }

    public SqlTransaction? Transaction { get; private set; }

    public void UseTransaction(SqlConnection connection, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The transaction does not belong to the provided connection.", nameof(transaction));
        }

        Connection = connection;
        Transaction = transaction;
    }

    public void Clear()
    {
        Connection = null;
        Transaction = null;
    }
}
