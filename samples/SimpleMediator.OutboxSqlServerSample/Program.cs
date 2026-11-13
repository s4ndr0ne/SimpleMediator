using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleMediator;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;
using SimpleMediator.Outbox.SqlServer;

// Set OUTBOX_SQLSERVER_CONNECTION to a live SQL Server instance to run end-to-end.
// Example:
//   export OUTBOX_SQLSERVER_CONNECTION="Server=localhost,1433;Database=OutboxDemo;User Id=sa;Password=Your_password123;TrustServerCertificate=True;"
var connectionString = Environment.GetEnvironmentVariable("OUTBOX_SQLSERVER_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("Set OUTBOX_SQLSERVER_CONNECTION to run this sample against SQL Server.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSimpleMediator(options =>
    options.RegisterAssembly(typeof(CreateOrderCommand).Assembly));

builder.Services.AddJsonOutboxSerializer(serializer =>
    serializer.AddNotification("order.created.v1", SampleJsonContext.Default.OrderCreatedNotification));

builder.Services.AddSqlServerOutboxStore(options =>
{
    options.ConnectionString = connectionString;
    options.SchemaName = "dbo";
    options.TableName = "OutboxMessages";
    options.MaxAttempts = 5;
    options.EnsureSchemaOnStartup = true;
    options.ProcessedMessageRetention = TimeSpan.FromDays(3);
    options.DeadLetterRetention = TimeSpan.FromDays(7);
});

builder.Services.AddSimpleMediatorOutbox(options =>
{
    options.BatchSize = 16;
    options.PollingInterval = TimeSpan.FromSeconds(2);
    options.RetryDelay = TimeSpan.FromSeconds(5);
    options.RetryBackoffMultiplier = 2;
    options.MaxRetryDelay = TimeSpan.FromMinutes(2);
});

builder.Services.AddSingleton(new DeliverySignal());

using var host = builder.Build();
await host.StartAsync();

try
{
    using var scope = host.Services.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var orderId = await mediator.Send(new CreateOrderCommand("Starter Pack", connectionString));
    Console.WriteLine($"Committed order {orderId} and enqueued outbox notification.");

    var signal = host.Services.GetRequiredService<DeliverySignal>();
    await signal.Delivered.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine("Sample completed.");
}
finally
{
    await host.StopAsync();
}

public sealed record CreateOrderCommand(string ItemName, string ConnectionString) : IRequest<Guid>;

public sealed record OrderCreatedNotification(Guid OrderId, string ItemName) : INotification;

public sealed class CreateOrderHandler(
    IOutboxPublisher outboxPublisher,
    ISqlServerOutboxSession outboxSession)
    : IRequestHandler<CreateOrderCommand, Guid>
{
    public async Task<Guid> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();

        await using var connection = new SqlConnection(request.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // BeginScope binds the transaction for this async flow and restores any outer binding on dispose.
        // SqlServerOutboxOptions.EnqueueRequiresTransaction (on by default) rejects enqueues outside it.
        using var outboxScope = outboxSession.BeginScope(connection, transaction);

        // Persist application entities on (connection, transaction) here...

        await outboxPublisher.EnqueueAsync(
            new OrderCreatedNotification(orderId, request.ItemName),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return orderId;
    }
}

public sealed class OrderCreatedHandler(DeliverySignal signal)
    : INotificationHandler<OrderCreatedNotification>
{
    public Task Handle(OrderCreatedNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Outbox dispatched OrderCreatedNotification: {notification.OrderId} ({notification.ItemName})");
        signal.MarkDelivered();
        return Task.CompletedTask;
    }
}

public sealed class DeliverySignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Delivered => _tcs.Task;

    public void MarkDelivered() => _tcs.TrySetResult();
}

[JsonSerializable(typeof(OrderCreatedNotification))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
