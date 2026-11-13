using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox.SqlServer.Tests;

public sealed partial class InMemoryDatabaseOutboxTests
{
    [Fact]
    public async Task Committed_transaction_persists_message_and_dispatcher_marks_it_processed()
    {
        var db = new InMemoryOutboxDatabase(maxAttempts: 5);
        var delivered = new TaskCompletionSource<OrderPlacedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = BuildServiceProvider(db, delivered);
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var svc in hostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        try
        {
            using (var scope = provider.CreateScope())
            {
                var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
                await using var tx = db.BeginTransaction();
                db.CurrentTransaction = tx;

                await publisher.EnqueueAsync(new OrderPlacedNotification(101, 49.90m));
                Assert.Empty(db.Rows); // Not visible until committed

                await tx.CommitAsync();
                db.CurrentTransaction = null;
            }

            var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(101, received.OrderId);
            Assert.Equal(49.90m, received.Total);

            // Wait briefly for MarkProcessedAsync to persist ProcessedAtUtc
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && db.Rows.Values.All(r => r.ProcessedAtUtc is null))
            {
                await Task.Delay(20);
            }

            var row = Assert.Single(db.Rows.Values);
            Assert.Equal("order.placed.v1", row.Type);
            Assert.NotNull(row.ProcessedAtUtc);
            Assert.Null(row.DeadLetteredAtUtc);
            Assert.Null(row.LeaseToken);
        }
        finally
        {
            foreach (var svc in hostedServices)
            {
                await svc.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Rolled_back_transaction_does_not_persist_message_or_dispatch()
    {
        var db = new InMemoryOutboxDatabase(maxAttempts: 5);
        var delivered = new TaskCompletionSource<OrderPlacedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var provider = BuildServiceProvider(db, delivered);
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var svc in hostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        try
        {
            using (var scope = provider.CreateScope())
            {
                var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
                await using var tx = db.BeginTransaction();
                db.CurrentTransaction = tx;

                await publisher.EnqueueAsync(new OrderPlacedNotification(202, 19.00m));
                await tx.RollbackAsync();
                db.CurrentTransaction = null;
            }

            await Task.Delay(150);
            Assert.Empty(db.Rows);
            Assert.False(delivered.Task.IsCompleted);
        }
        finally
        {
            foreach (var svc in hostedServices)
            {
                await svc.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Failing_handler_retries_and_dead_letters_after_max_attempts()
    {
        var db = new InMemoryOutboxDatabase(maxAttempts: 2);
        var attemptsObserved = 0;

        var services = new ServiceCollection();
        services.AddSimpleMediator(cfg => cfg.RegisterAssembly(typeof(InMemoryDatabaseOutboxTests).Assembly));
        services.AddSingleton(new OrderPlacedCallback(_ =>
        {
            Interlocked.Increment(ref attemptsObserved);
            throw new InvalidOperationException("Simulated downstream failure");
        }));
        services.AddJsonOutboxSerializer(b =>
            b.AddNotification("order.placed.v1", InMemoryTestJsonContext.Default.OrderPlacedNotification));
        services.AddSingleton(db);
        services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxDatabase>());
        services.AddSimpleMediatorOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(30);
            options.RetryDelay = TimeSpan.FromMilliseconds(10);
        });

        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var svc in hostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        try
        {
            using (var scope = provider.CreateScope())
            {
                var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
                await publisher.EnqueueAsync(new OrderPlacedNotification(303, 99.00m));
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && db.Rows.Values.All(r => r.DeadLetteredAtUtc is null))
            {
                await Task.Delay(25);
            }

            var row = Assert.Single(db.Rows.Values);
            Assert.Equal(2, row.Attempts);
            Assert.Equal(2, Volatile.Read(ref attemptsObserved));
            Assert.NotNull(row.DeadLetteredAtUtc);
            Assert.Null(row.ProcessedAtUtc);
            Assert.Contains("Simulated downstream failure", row.LastError, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var svc in hostedServices)
            {
                await svc.StopAsync(CancellationToken.None);
            }
        }
    }

    private static ServiceProvider BuildServiceProvider(
        InMemoryOutboxDatabase db,
        TaskCompletionSource<OrderPlacedNotification> delivered)
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(cfg => cfg.RegisterAssembly(typeof(InMemoryDatabaseOutboxTests).Assembly));
        services.AddSingleton(new OrderPlacedCallback(notification =>
        {
            delivered.TrySetResult(notification);
            return Task.CompletedTask;
        }));
        services.AddJsonOutboxSerializer(b =>
            b.AddNotification("order.placed.v1", InMemoryTestJsonContext.Default.OrderPlacedNotification));
        services.AddSingleton(db);
        services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxDatabase>());
        services.AddSimpleMediatorOutbox(options =>
        {
            options.PollingInterval = TimeSpan.FromMilliseconds(50);
            options.RetryDelay = TimeSpan.FromMilliseconds(20);
        });
        return services.BuildServiceProvider();
    }

    public sealed record OrderPlacedNotification(int OrderId, decimal Total) : INotification;

    public sealed record OrderPlacedCallback(Func<OrderPlacedNotification, Task> Invoke);

    public sealed class OrderPlacedHandler(OrderPlacedCallback callback)
        : INotificationHandler<OrderPlacedNotification>
    {
        public Task Handle(OrderPlacedNotification notification, CancellationToken cancellationToken)
            => callback.Invoke(notification);
    }

    [JsonSerializable(typeof(OrderPlacedNotification))]
    private sealed partial class InMemoryTestJsonContext : JsonSerializerContext;

    private sealed class InMemoryOutboxRow
    {
        public required Guid Id { get; init; }
        public required string Type { get; init; }
        public required string Payload { get; init; }
        public required DateTimeOffset OccurredAtUtc { get; init; }
        public DateTimeOffset AvailableAtUtc { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public Guid? LeaseToken { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public DateTimeOffset? ProcessedAtUtc { get; set; }
        public DateTimeOffset? DeadLetteredAtUtc { get; set; }
    }

    private sealed class InMemoryTransaction(InMemoryOutboxDatabase db) : IAsyncDisposable
    {
        public List<InMemoryOutboxRow> Staged { get; } = [];
        public bool Completed { get; private set; }

        public Task CommitAsync()
        {
            if (Completed)
            {
                return Task.CompletedTask;
            }

            Completed = true;
            foreach (var row in Staged)
            {
                db.Rows[row.Id] = row;
            }

            return Task.CompletedTask;
        }

        public Task RollbackAsync()
        {
            Completed = true;
            Staged.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!Completed)
            {
                Staged.Clear();
                Completed = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryOutboxDatabase(int maxAttempts) : IOutboxStore
    {
        private readonly object _gate = new();

        public ConcurrentDictionary<Guid, InMemoryOutboxRow> Rows { get; } = new();

        public InMemoryTransaction? CurrentTransaction { get; set; }

        public InMemoryTransaction BeginTransaction() => new(this);

        public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            var row = new InMemoryOutboxRow
            {
                Id = message.Id,
                Type = message.Type,
                Payload = message.Payload,
                OccurredAtUtc = message.OccurredAtUtc,
                AvailableAtUtc = message.OccurredAtUtc,
            };

            if (CurrentTransaction is { Completed: false } tx)
            {
                tx.Staged.Add(row);
            }
            else
            {
                Rows[row.Id] = row;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
            int batchSize,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var candidates = Rows.Values
                    .Where(r => r.ProcessedAtUtc is null
                        && r.DeadLetteredAtUtc is null
                        && r.AvailableAtUtc <= nowUtc
                        && (r.LeaseExpiresAtUtc is null || r.LeaseExpiresAtUtc <= nowUtc))
                    .OrderBy(r => r.AvailableAtUtc)
                    .ThenBy(r => r.OccurredAtUtc)
                    .Take(batchSize)
                    .ToList();

                var leases = new List<OutboxLease>(candidates.Count);
                foreach (var row in candidates)
                {
                    var token = Guid.NewGuid();
                    row.LeaseToken = token;
                    row.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);

                    leases.Add(new OutboxLease(
                        new OutboxMessage(row.Id, row.Type, row.Payload, row.OccurredAtUtc),
                        token));
                }

                return Task.FromResult<IReadOnlyList<OutboxLease>>(leases);
            }
        }

        public Task<bool> RenewLeaseAsync(
            OutboxLease lease,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!Rows.TryGetValue(lease.Message.Id, out var row)
                    || row.LeaseToken != lease.LeaseToken
                    || row.ProcessedAtUtc is not null
                    || row.DeadLetteredAtUtc is not null
                    || row.LeaseExpiresAtUtc <= nowUtc)
                {
                    return Task.FromResult(false);
                }

                row.LeaseExpiresAtUtc = nowUtc.Add(leaseDuration);
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkProcessedAsync(
            OutboxLease lease,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!Rows.TryGetValue(lease.Message.Id, out var row)
                    || row.LeaseToken != lease.LeaseToken
                    || row.ProcessedAtUtc is not null
                    || row.DeadLetteredAtUtc is not null)
                {
                    return Task.FromResult(false);
                }

                row.ProcessedAtUtc = processedAtUtc;
                row.LeaseToken = null;
                row.LeaseExpiresAtUtc = null;
                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkFailedAsync(
            OutboxLease lease,
            DateTimeOffset retryAtUtc,
            string error,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!Rows.TryGetValue(lease.Message.Id, out var row)
                    || row.LeaseToken != lease.LeaseToken
                    || row.ProcessedAtUtc is not null
                    || row.DeadLetteredAtUtc is not null)
                {
                    return Task.FromResult(false);
                }

                row.Attempts++;
                row.LastError = error;
                row.AvailableAtUtc = retryAtUtc;
                row.LeaseToken = null;
                row.LeaseExpiresAtUtc = null;
                if (row.Attempts >= maxAttempts)
                {
                    row.DeadLetteredAtUtc = DateTimeOffset.UtcNow;
                }

                return Task.FromResult(true);
            }
        }
    }
}
