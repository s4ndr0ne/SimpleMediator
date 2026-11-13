using Microsoft.Data.SqlClient;

namespace SimpleMediator.Outbox.SqlServer;

/// <summary>
/// Scoped ambient SQL Server connection and transaction used by <see cref="SqlServerOutboxStore.EnqueueAsync"/>
/// so outbox writes participate in the application's current transaction.
/// </summary>
public interface ISqlServerOutboxSession
{
    /// <summary>The active SQL Server connection, if bound.</summary>
    SqlConnection? Connection { get; }

    /// <summary>The active SQL Server transaction, if bound.</summary>
    SqlTransaction? Transaction { get; }

    /// <summary>Binds an existing <see cref="SqlConnection"/> and optional <see cref="SqlTransaction"/> to the current scope.</summary>
    void UseTransaction(SqlConnection connection, SqlTransaction? transaction = null);

    /// <summary>Clears any bound connection and transaction from the current scope.</summary>
    void Clear();
}
