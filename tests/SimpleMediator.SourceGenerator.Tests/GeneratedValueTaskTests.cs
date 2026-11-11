using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.SourceGenerator.Tests;

public sealed class GeneratedValueTaskTests
{
    public sealed record Ping : IRequest<int>;
    public sealed class PingHandler : IValueTaskRequestHandler<Ping, int>
    {
        public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(42);
    }

    public sealed record Box<T>(T Value) : IRequest<T>;
    public sealed class BoxHandler : IValueTaskRequestHandler<Box<int>, int>
    {
        public ValueTask<int> Handle(Box<int> request, CancellationToken cancellationToken) => new(request.Value);
    }

    [Fact]
    public async Task Generated_scan_registers_value_task_handlers()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorGenerated(options => options.RegisterAssembly(typeof(Ping).Assembly, type => type == typeof(PingHandler)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(42, await sender.SendValueTask(new Ping()));
        Assert.Equal(42, await sender.SendValueTask(new Ping()));
    }

    [Fact]
    public async Task Generated_dispatch_discovers_generic_request_sent_by_value_task()
    {
        var services = new ServiceCollection();
        services.AddTransient<IValueTaskRequestHandler<Box<int>, int>, BoxHandler>();
        services.AddSimpleMediatorGenerated();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(7, await sender.SendValueTask(new Box<int>(7)));
        Assert.Equal(8, await SenderExtensions.SendValueTask(sender, new Box<int>(8)));
    }

    [Fact]
    public void ValidateOnBuild_rejects_task_and_value_task_handlers_for_the_same_request()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<Ping, int>, TaskPingHandler>();

        Assert.Throws<RequestHandlerResolutionException>(() => services.AddSimpleMediatorGenerated(options =>
        {
            options.RegisterAssembly(typeof(Ping).Assembly, type => type == typeof(PingHandler));
            options.ValidateOnBuild = true;
        }));
    }

    public sealed class TaskPingHandler : IRequestHandler<Ping, int>
    {
        public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(42);
    }
}
