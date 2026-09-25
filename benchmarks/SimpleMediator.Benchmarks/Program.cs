using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;

BenchmarkRunner.Run<MediatorBenchmarks>(DefaultConfig.Instance.WithArtifactsPath("BenchmarkDotNet.Artifacts"));

[MemoryDiagnoser]
public class MediatorBenchmarks
{
    private ServiceProvider _provider = null!;
    private ServiceProvider _parallelProvider = null!;
    private IMediator _mediator = null!;
    private IMediator _parallelMediator = null!;
    private PingHandler _directHandler = null!;
    private PingRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(PingHandler).Assembly));
        _provider = services.BuildServiceProvider();
        _mediator = _provider.GetRequiredService<IMediator>();

        var parallelServices = new ServiceCollection();
        parallelServices.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(PingHandler).Assembly);
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
        });
        _parallelProvider = parallelServices.BuildServiceProvider();
        _parallelMediator = _parallelProvider.GetRequiredService<IMediator>();

        _directHandler = new PingHandler();
        _request = new PingRequest(42);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _provider.Dispose();
        _parallelProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<int> MediatorSend() => _mediator.Send(_request);

    [Benchmark]
    public Task<int> DirectHandlerCall() => _directHandler.Handle(_request, CancellationToken.None);

    [Benchmark]
    public Task PublishSequential() => _mediator.Publish(new PingNotification(42));

    [Benchmark]
    public Task PublishParallel() => _parallelMediator.Publish(new PingNotification(42));
}

public sealed record PingRequest(int Value) : IRequest<int>;
public sealed record PingNotification(int Value) : INotification;

public sealed class PingHandler : IRequestHandler<PingRequest, int>
{
    public Task<int> Handle(PingRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value + 1);
}

public sealed class PingNotificationHandler : INotificationHandler<PingNotification>
{
    public Task Handle(PingNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
