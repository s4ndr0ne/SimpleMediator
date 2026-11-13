using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SimpleMediator.Outbox.SqlServer.Tests;

public sealed class SqlServerOutboxTests
{
    private const string TestConnectionString =
        "Server=localhost;Database=OutboxTest;Integrated Security=true;TrustServerCertificate=true;";

    [Fact]
    public void Sql_generates_expected_qualified_names_and_locking_clauses()
    {
        var sql = new SqlServerOutboxSql("messaging", "CustomOutbox");

        Assert.Equal("[messaging].[CustomOutbox]", sql.QualifiedTableName);
        Assert.Contains("WITH (UPDLOCK, READPAST, ROWLOCK)", sql.ClaimBatchSql, StringComparison.Ordinal);
        Assert.Contains("SET LeaseToken = NEWID()", sql.ClaimBatchSql, StringComparison.Ordinal);
        Assert.Contains("inserted.Attempts", sql.ClaimBatchSql, StringComparison.Ordinal);
        Assert.Contains("DeadLetteredAtUtc = CASE WHEN Attempts + 1 >= @MaxAttempts THEN @NowUtc ELSE NULL END", sql.MarkFailedSql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [messaging].[CustomOutbox]", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("IX_CustomOutbox_Pending", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("IX_CustomOutbox_Processed", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("IX_CustomOutbox_DeadLettered", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("sp_getapplock", sql.EnsureSchemaSql, StringComparison.Ordinal);
        Assert.Contains("DELETE TOP (@BatchSize)", sql.PurgeProcessedSql, StringComparison.Ordinal);
        Assert.Contains("DELETE TOP (@BatchSize)", sql.PurgeDeadLetteredSql, StringComparison.Ordinal);
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
    public void Sql_rejects_table_names_that_would_overflow_derived_constraint_names()
    {
        var tooLong = new string('a', 101);

        Assert.Throws<ArgumentException>(() => new SqlServerOutboxSql("dbo", tooLong));
        Assert.True(SqlServerOutboxSql.IsValidTableName(new string('a', 100)));
    }

    [Fact]
    public void Session_tracks_and_clears_connection_and_transaction()
    {
        var session = new SqlServerOutboxSession();
        using var connection = new SqlConnection(TestConnectionString);

        session.UseTransaction(connection);
        Assert.Same(connection, session.Connection);
        Assert.Null(session.Transaction);

        session.Clear();
        Assert.Null(session.Connection);
        Assert.Null(session.Transaction);
    }

    [Fact]
    public void BeginScope_restores_the_previous_binding_when_disposed()
    {
        var session = new SqlServerOutboxSession();
        using var outer = new SqlConnection(TestConnectionString);
        using var inner = new SqlConnection(TestConnectionString);

        session.UseTransaction(outer);
        using (session.BeginScope(inner))
        {
            Assert.Same(inner, session.Connection);
        }

        Assert.Same(outer, session.Connection);

        session.Clear();
        Assert.Null(session.Connection);
    }

    [Fact]
    public async Task BeginScope_is_isolated_across_parallel_async_flows()
    {
        var session = new SqlServerOutboxSession();
        using var first = new SqlConnection(TestConnectionString);
        using var second = new SqlConnection(TestConnectionString);

        await Task.WhenAll(
            Task.Run(async () =>
            {
                using var scope = session.BeginScope(first);
                await Task.Delay(50);
                Assert.Same(first, session.Connection);
            }),
            Task.Run(async () =>
            {
                using var scope = session.BeginScope(second);
                await Task.Delay(50);
                Assert.Same(second, session.Connection);
            }));

        Assert.Null(session.Connection);
    }

    [Fact]
    public void AddSqlServerOutboxStore_registers_services_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddSqlServerOutboxStore(options =>
        {
            options.ConnectionString = TestConnectionString;
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
        Assert.Contains(hostedServices, service => service is SqlServerOutboxSchemaHostedService);
        Assert.Contains(hostedServices, service => service is SqlServerOutboxPurgeHostedService);

        var options = provider.GetRequiredService<IOptions<SqlServerOutboxOptions>>().Value;
        Assert.Equal("app", options.SchemaName);
        Assert.Equal("OutboxQueue", options.TableName);
        Assert.Equal(8, options.MaxAttempts);
        Assert.True(options.EnqueueRequiresTransaction);
        Assert.Equal(TimeSpan.FromDays(7), options.ProcessedMessageRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.DeadLetterRetention);
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

    [Theory]
    [InlineData("retention")]
    [InlineData("purge-interval")]
    [InlineData("purge-batch")]
    [InlineData("command-timeout")]
    [InlineData("max-attempts")]
    [InlineData("table-name")]
    public void AddSqlServerOutboxStore_validation_rejects_new_option_violations(string property)
    {
        var services = new ServiceCollection();
        services.AddSqlServerOutboxStore(options =>
        {
            options.ConnectionString = TestConnectionString;
            switch (property)
            {
                case "retention":
                    options.ProcessedMessageRetention = TimeSpan.FromSeconds(-1);
                    break;
                case "purge-interval":
                    options.PurgeInterval = TimeSpan.Zero;
                    break;
                case "purge-batch":
                    options.PurgeBatchSize = 0;
                    break;
                case "command-timeout":
                    options.CommandTimeout = TimeSpan.Zero;
                    break;
                case "max-attempts":
                    options.MaxAttempts = 1001;
                    break;
                default:
                    options.TableName = new string('t', 101);
                    break;
            }
        });

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<SqlServerOutboxOptions>>().Value);
    }
}
