using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SimpleMediator.Outbox.SqlServer.Tests;

public sealed class SqlServerOutboxTests
{
    [Fact]
    public void Sql_generates_expected_qualified_names_and_locking_clauses()
    {
        var sql = new SqlServerOutboxSql("messaging", "CustomOutbox");

        Assert.Equal("[messaging].[CustomOutbox]", sql.QualifiedTableName);
        Assert.Contains("WITH (UPDLOCK, READPAST, ROWLOCK)", sql.ClaimBatchSql, StringComparison.Ordinal);
        Assert.Contains("SET LeaseToken = NEWID()", sql.ClaimBatchSql, StringComparison.Ordinal);
        Assert.Contains("DeadLetteredAtUtc = CASE WHEN Attempts + 1 >= @MaxAttempts THEN @NowUtc ELSE NULL END", sql.MarkFailedSql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [messaging].[CustomOutbox]", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("IX_CustomOutbox_Pending", sql.EnsureSchemaSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123bad")]
    [InlineData("dbo; DROP TABLE Users;--")]
    [InlineData("outbox]--")]
    [InlineData("out.box")]
    public void Sql_rejects_invalid_identifiers(string invalidIdentifier)
    {
        Assert.Throws<ArgumentException>(() => new SqlServerOutboxSql(invalidIdentifier, "OutboxMessages"));
        Assert.Throws<ArgumentException>(() => new SqlServerOutboxSql("dbo", invalidIdentifier));
    }

    [Fact]
    public void Session_tracks_and_clears_connection_and_transaction()
    {
        var session = new SqlServerOutboxSession();
        using var connection = new SqlConnection("Server=localhost;Database=OutboxTest;Integrated Security=true;TrustServerCertificate=true;");

        session.UseTransaction(connection);
        Assert.Same(connection, session.Connection);
        Assert.Null(session.Transaction);

        session.Clear();
        Assert.Null(session.Connection);
        Assert.Null(session.Transaction);
    }

    [Fact]
    public void AddSqlServerOutboxStore_registers_services_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddSqlServerOutboxStore(options =>
        {
            options.ConnectionString = "Server=localhost;Database=OutboxTest;TrustServerCertificate=true;";
            options.SchemaName = "app";
            options.TableName = "OutboxQueue";
            options.MaxAttempts = 8;
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var session = scope.ServiceProvider.GetRequiredService<ISqlServerOutboxSession>();
        var hostedServices = provider.GetServices<IHostedService>();

        Assert.IsType<SqlServerOutboxStore>(store);
        Assert.IsType<SqlServerOutboxSession>(session);
        Assert.Contains(hostedServices, s => s is SqlServerOutboxSchemaHostedService);

        var options = provider.GetRequiredService<IOptions<SqlServerOutboxOptions>>().Value;
        Assert.Equal("app", options.SchemaName);
        Assert.Equal("OutboxQueue", options.TableName);
        Assert.Equal(8, options.MaxAttempts);
    }

    [Fact]
    public void AddSqlServerOutboxStore_validation_rejects_invalid_options()
    {
        var services = new ServiceCollection();
        services.AddSqlServerOutboxStore(options =>
        {
            options.ConnectionString = "";
            options.SchemaName = "bad;schema";
            options.MaxAttempts = 0;
        });

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SqlServerOutboxOptions>>().Value);
    }
}
