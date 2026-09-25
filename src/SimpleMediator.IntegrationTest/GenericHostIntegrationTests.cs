using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleMediator;
using SimpleMediator.Interfaces;

namespace SimpleMediator.IntegrationTest;

public sealed class GenericHostIntegrationTests
{
    [Fact]
    public async Task GenericHost_ResolvesMediatorInsideScope_AndKeepsScopeIdentity()
    {
        using var host = CreateHost();
        await host.StartAsync();

        try
        {
            using var firstScope = host.Services.CreateScope();
            var firstMediator = firstScope.ServiceProvider.GetRequiredService<IMediator>();
            var first = await firstMediator.Send(new IntegrationScopeRequest());
            var second = await firstMediator.Send(new IntegrationScopeRequest());

            using var secondScope = host.Services.CreateScope();
            var secondMediator = secondScope.ServiceProvider.GetRequiredService<IMediator>();
            var third = await secondMediator.Send(new IntegrationScopeRequest());

            Assert.Equal(first, second);
            Assert.NotEqual(first, third);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task GenericHost_RejectsMediatorUseFromRoot_WhenScopeValidationIsEnabled()
    {
        using var host = CreateHost();
        var mediator = host.Services.GetRequiredService<IMediator>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new IntegrationScopeRequest()));

        Assert.Contains("scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericHost_ConcurrentMediationUsesIndependentScopes()
    {
        using var host = CreateHost();
        await host.StartAsync();

        try
        {
            var tasks = Enumerable.Range(0, 32).Select(async _ =>
            {
                using var scope = host.Services.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                return await mediator.Send(new IntegrationScopeRequest());
            });

            var ids = await Task.WhenAll(tasks);

            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task GenericHost_DisposesAsyncOpenGenericHandlerAfterRequest()
    {
        using var host = CreateHost();
        await host.StartAsync();

        try
        {
            var probe = host.Services.GetRequiredService<AsyncDisposeProbe>();
            using var scope = host.Services.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var result = await mediator.Send(new IntegrationOpenGenericRequest<int>(42));

            Assert.Equal(42, result);
            Assert.Equal(1, probe.DisposeCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task GenericHost_PublishesNotificationsSequentiallyByDefault()
    {
        using var host = CreateHost();
        await host.StartAsync();

        try
        {
            var probe = host.Services.GetRequiredService<ConcurrencyProbe>();
            using var scope = host.Services.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            await mediator.Publish(new IntegrationNotification());

            Assert.Equal(2, probe.Entered);
            Assert.Equal(1, probe.MaxObserved);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost CreateHost()
    {
        return Host.CreateDefaultBuilder()
            .UseDefaultServiceProvider((_, options) =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<AsyncDisposeProbe>();
                services.AddSingleton<ConcurrencyProbe>();
                services.AddScoped<IntegrationScopeProbe>();
                services.AddSimpleMediator(options =>
                {
                    options.DefaultLifetime = ServiceLifetime.Scoped;
                    options.RegisterAssembly(typeof(GenericHostIntegrationTests).Assembly);
                });
            })
            .Build();
    }
}

public sealed record IntegrationScopeRequest : IRequest<Guid>;

public sealed class IntegrationScopeProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class IntegrationScopeHandler : IRequestHandler<IntegrationScopeRequest, Guid>
{
    private readonly IntegrationScopeProbe _probe;

    public IntegrationScopeHandler(IntegrationScopeProbe probe) => _probe = probe;

    public Task<Guid> Handle(IntegrationScopeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(_probe.Id);
}

public sealed record IntegrationOpenGenericRequest<T>(T Value) : IRequest<T>;

public sealed class IntegrationOpenGenericHandler<T> :
    IRequestHandler<IntegrationOpenGenericRequest<T>, T>,
    IAsyncDisposable
{
    private readonly AsyncDisposeProbe _probe;

    public IntegrationOpenGenericHandler(AsyncDisposeProbe probe) => _probe = probe;

    public Task<T> Handle(IntegrationOpenGenericRequest<T> request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);

    public ValueTask DisposeAsync()
    {
        _probe.RecordDispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AsyncDisposeProbe
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void RecordDispose() => Interlocked.Increment(ref _disposeCount);
}

public sealed record IntegrationNotification : INotification;

public sealed class FirstIntegrationNotificationHandler : INotificationHandler<IntegrationNotification>
{
    private readonly ConcurrencyProbe _probe;

    public FirstIntegrationNotificationHandler(ConcurrencyProbe probe) => _probe = probe;

    public Task Handle(IntegrationNotification notification, CancellationToken cancellationToken)
        => _probe.EnterAndWait(cancellationToken);
}

public sealed class SecondIntegrationNotificationHandler : INotificationHandler<IntegrationNotification>
{
    private readonly ConcurrencyProbe _probe;

    public SecondIntegrationNotificationHandler(ConcurrencyProbe probe) => _probe = probe;

    public Task Handle(IntegrationNotification notification, CancellationToken cancellationToken)
        => _probe.EnterAndWait(cancellationToken);
}

public sealed class ConcurrencyProbe
{
    private int _current;
    private int _max;
    private int _entered;

    public int MaxObserved => Volatile.Read(ref _max);
    public int Entered => Volatile.Read(ref _entered);

    public async Task EnterAndWait(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _entered);
        var current = Interlocked.Increment(ref _current);
        UpdateMaximum(current);

        try
        {
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        var snapshot = Volatile.Read(ref _max);
        while (candidate > snapshot)
        {
            var observed = Interlocked.CompareExchange(ref _max, candidate, snapshot);
            if (observed == snapshot)
            {
                return;
            }

            snapshot = observed;
        }
    }
}
