using Microsoft.Data.SqlClient;

namespace SimpleMediator.Outbox.SqlServer;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's dependency-injection registration.")]
internal sealed class SqlServerOutboxSession : ISqlServerOutboxSession
{
    // Per-instance AsyncLocal keeps the binding scoped to the current async flow: parallel handlers in
    // the same DI scope each see their own connection instead of racing on shared mutable state.
    private readonly AsyncLocal<Binding?> _current = new();

    public SqlConnection? Connection => _current.Value?.Connection;

    public SqlTransaction? Transaction => _current.Value?.Transaction;

    public void UseTransaction(SqlConnection connection, SqlTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The transaction does not belong to the provided connection.", nameof(transaction));
        }

        _current.Value = new Binding(connection, transaction, _current.Value);
    }

    public IDisposable BeginScope(SqlConnection connection, SqlTransaction? transaction = null)
    {
        UseTransaction(connection, transaction);
        return new Scope(this);
    }

    public void Clear() => _current.Value = _current.Value?.Parent;

    private sealed record Binding(SqlConnection Connection, SqlTransaction? Transaction, Binding? Parent);

    private sealed class Scope(SqlServerOutboxSession session) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session.Clear();
        }
    }
}
