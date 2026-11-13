using Microsoft.Data.SqlClient;

namespace SimpleMediator.Outbox.SqlServer;

/// <summary>
/// Scoped ambient SQL Server connection and transaction used by <see cref="SqlServerOutboxStore.EnqueueAsync"/>
/// so outbox writes participate in the application's current transaction.
/// </summary>
/// <remarks>
/// The binding is stored per async flow, so concurrent handlers in the same scope cannot overwrite each
/// other's connection. Bindings nest: <see cref="BeginScope"/> restores the previous binding when disposed,
/// while <see cref="Clear"/> removes the innermost one.
/// </remarks>
public interface ISqlServerOutboxSession
{
    /// <summary>The active SQL Server connection, if bound.</summary>
    SqlConnection? Connection { get; }

    /// <summary>The active SQL Server transaction, if bound.</summary>
    SqlTransaction? Transaction { get; }

    /// <summary>Binds an existing <see cref="SqlConnection"/> and optional <see cref="SqlTransaction"/> to the current scope.</summary>
    /// <param name="connection">The connection owned by the application.</param>
    /// <param name="transaction">The transaction to enlist outbox writes in, or <see langword="null"/> for a non-transactional binding.</param>
    void UseTransaction(SqlConnection connection, SqlTransaction? transaction = null);

    /// <summary>
    /// Binds a connection and transaction and restores the previous binding when the returned scope is disposed.
    /// Prefer this over <see cref="UseTransaction"/> plus <see cref="Clear"/> because it is exception-safe and nesting-safe.
    /// </summary>
    /// <param name="connection">The connection owned by the application.</param>
    /// <param name="transaction">The transaction to enlist outbox writes in, or <see langword="null"/> for a non-transactional binding.</param>
    /// <returns>A scope that restores the previous binding when disposed.</returns>
    IDisposable BeginScope(SqlConnection connection, SqlTransaction? transaction = null);

    /// <summary>Removes the innermost binding from the current async flow.</summary>
    void Clear();
}
