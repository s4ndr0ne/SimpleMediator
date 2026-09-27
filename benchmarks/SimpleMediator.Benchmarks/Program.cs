using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;

BenchmarkSwitcher.FromAssembly(typeof(MediatorBenchmarks).Assembly)
    .Run(args, DefaultConfig.Instance.WithArtifactsPath("BenchmarkDotNet.Artifacts"));

// Every mediator is resolved from a scope, which is the only supported usage. The baseline is an
// ASYNC handler resolved and called through DI, so the comparison isolates mediator dispatch
// overhead instead of measuring the difference between a completed task and a state machine.
[MemoryDiagnoser]
public class MediatorBenchmarks
{
    private ServiceProvider _provider = null!;
    private ServiceProvider _parallelProvider = null!;
    private IServiceScope _scope = null!;
    private IServiceScope _parallelScope = null!;
    private IMediator _mediator = null!;
    private IMediator _parallelMediator = null!;
    private IRequestHandler<PingRequest, int> _directHandler = null!;
    private PingRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(PingHandler).Assembly));
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _mediator = _scope.ServiceProvider.GetRequiredService<IMediator>();
        _directHandler = _scope.ServiceProvider.GetRequiredService<IRequestHandler<PingRequest, int>>();

        var parallelServices = new ServiceCollection();
        parallelServices.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(PingHandler).Assembly);
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
        });
        _parallelProvider = parallelServices.BuildServiceProvider();
        _parallelScope = _parallelProvider.CreateScope();
        _parallelMediator = _parallelScope.ServiceProvider.GetRequiredService<IMediator>();

        _request = new PingRequest(42);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _parallelScope.Dispose();
        _provider.Dispose();
        _parallelProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<int> MediatorSend() => _mediator.Send(_request);

    [Benchmark]
    public Task<int> DirectHandlerCall() => _directHandler.Handle(_request, CancellationToken.None);

    [Benchmark]
    public Task<int> MediatorSend_WithBehaviors() => _behavioralMediator.Send(_request);

    [Benchmark]
    public Task<int> DirectHandlerCall_AsyncHandler() => _asyncHandler.Handle(_request, CancellationToken.None);

    [Benchmark]
    public Task PublishSequential() => _mediator.Publish(new PingNotification(42));

    [Benchmark]
    public Task PublishParallel() => _parallelMediator.Publish(new PingNotification(42));

    private IMediator _behavioralMediator = null!;
    private PingHandler _asyncHandler = null!;

    [GlobalSetup(Target = nameof(MediatorSend_WithBehaviors))]
    public void SetupBehaviors()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(PingHandler).Assembly);
            options.AddBehavior(typeof(CountingBehaviorOne));
            options.AddBehavior(typeof(CountingBehaviorTwo));
            options.AddBehavior(typeof(CountingBehaviorThree));
        });
        var provider = services.BuildServiceProvider();
        _behavioralMediator = provider.CreateScope().ServiceProvider.GetRequiredService<IMediator>();
        _asyncHandler = new PingHandler();
    }
}

public sealed record PingRequest(int Value) : IRequest<int>;
public sealed record PingNotification(int Value) : INotification;

public sealed class PingHandler : IRequestHandler<PingRequest, int>
{
    // Deliberately async so the direct-call baseline pays the same state-machine cost as the
    // mediator path. Task.FromResult in the baseline would inflate the ratio.
    public async Task<int> Handle(PingRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return request.Value + 1;
    }
}

public sealed class PingNotificationHandler : INotificationHandler<PingNotification>
{
    public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class CountingBehaviorOne : IPipelineBehavior<PingRequest, int>
{
    public int Order => 0;

    public Task<int> Handle(PingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class CountingBehaviorTwo : IPipelineBehavior<PingRequest, int>
{
    public int Order => 1;

    public Task<int> Handle(PingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class CountingBehaviorThree : IPipelineBehavior<PingRequest, int>
{
    public int Order => 2;

    public Task<int> Handle(PingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

[MemoryDiagnoser]
public class SourceGeneratorBatchBenchmarks
{
    private const int CallCount = 50;

    private ServiceProvider _reflectionProvider = null!;
    private ServiceProvider _generatedProvider = null!;
    private IServiceScope _reflectionScope = null!;
    private IServiceScope _generatedScope = null!;
    private ISender _reflectionSender = null!;
    private ISender _generatedSender = null!;
    private BulkPingRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        var reflectionServices = new ServiceCollection();
        reflectionServices.AddSimpleMediator(options =>
            options.RegisterAssembly(typeof(BulkPingHandler).Assembly));
        _reflectionProvider = reflectionServices.BuildServiceProvider();
        _reflectionScope = _reflectionProvider.CreateScope();
        _reflectionSender = _reflectionScope.ServiceProvider.GetRequiredService<ISender>();

        var generatedServices = new ServiceCollection();
        generatedServices.AddSimpleMediatorGenerated(options =>
            options.RegisterAssembly(typeof(BulkPingHandler).Assembly));
        _generatedProvider = generatedServices.BuildServiceProvider();
        _generatedScope = _generatedProvider.CreateScope();
        _generatedSender = _generatedScope.ServiceProvider.GetRequiredService<ISender>();

        _request = new BulkPingRequest(42);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reflectionScope.Dispose();
        _generatedScope.Dispose();
        _reflectionProvider.Dispose();
        _generatedProvider.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = CallCount)]
    public async Task<int> Reflection_50Sends()
    {
        var total = 0;
        for (var i = 0; i < CallCount; i++)
        {
            total += await _reflectionSender.Send(_request);
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = CallCount)]
    public async Task<int> SourceGenerated_50Sends()
    {
        var total = 0;
        for (var i = 0; i < CallCount; i++)
        {
            total += await _generatedSender.Send(_request);
        }

        return total;
    }
}

public sealed record BulkPingRequest(int Value) : IRequest<int>;

public sealed class BulkPingHandler : IRequestHandler<BulkPingRequest, int>
{
    public Task<int> Handle(BulkPingRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value + 1);
}
