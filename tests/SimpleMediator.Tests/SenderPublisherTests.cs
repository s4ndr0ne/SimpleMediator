using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Tests;

public class SenderPublisherTests
{
    [Fact]
    public void IMediator_ExtendsSenderAndPublisher()
    {
        Assert.True(typeof(ISender).IsAssignableFrom(typeof(IMediator)));
        Assert.True(typeof(IPublisher).IsAssignableFrom(typeof(IMediator)));
        Assert.Empty(typeof(IMediator).GetMethods());
    }

    [Fact]
    public async Task AddSimpleMediator_RegistersSender_ThatDispatchesRequests()
    {
        using var root = BuildProvider();
        await using var scope = root.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal("echo:hi", await sender.Send(new SplitEchoRequest("hi")));
        await sender.Send(new SplitVoidRequest());
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<SplitProbe>().VoidCalls);
    }

    [Fact]
    public async Task AddSimpleMediator_RegistersPublisher_ThatDispatchesNotifications()
    {
        using var root = BuildProvider();
        await using var scope = root.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
        await publisher.Publish(new SplitNotification());

        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<SplitProbe>().Notifications);
    }

    [Fact]
    public async Task SenderAndPublisher_UseTheScopeTheyWereResolvedFrom()
    {
        using var root = BuildProvider();
        await using var first = root.CreateAsyncScope();
        await using var second = root.CreateAsyncScope();

        await first.ServiceProvider.GetRequiredService<IPublisher>().Publish(new SplitNotification());
        await first.ServiceProvider.GetRequiredService<ISender>().Send(new SplitVoidRequest());

        var firstProbe = first.ServiceProvider.GetRequiredService<SplitProbe>();
        var secondProbe = second.ServiceProvider.GetRequiredService<SplitProbe>();
        Assert.Equal(1, firstProbe.Notifications);
        Assert.Equal(1, firstProbe.VoidCalls);
        Assert.Equal(0, secondProbe.Notifications);
        Assert.Equal(0, secondProbe.VoidCalls);
    }

    [Fact]
    public void SenderAndPublisher_ForwardToAReplacedMediatorRegistration()
    {
        var services = new ServiceCollection();
        services.AddTransient<IMediator, FakeMediator>();
        services.AddSimpleMediator();

        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();

        Assert.IsType<FakeMediator>(scope.ServiceProvider.GetRequiredService<ISender>());
        Assert.IsType<FakeMediator>(scope.ServiceProvider.GetRequiredService<IPublisher>());
    }

    [Fact]
    public void AddSimpleMediator_CalledTwice_RegistersSenderAndPublisherOnce()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        services.AddSimpleMediator();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISender));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPublisher));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<SplitProbe>();
        services.AddTransient<IRequestHandler<SplitEchoRequest, string>, SplitEchoHandler>();
        services.AddTransient<IRequestHandler<SplitVoidRequest, Unit>, SplitVoidHandler>();
        services.AddTransient<INotificationHandler<SplitNotification>, SplitNotificationHandler>();
        services.AddSimpleMediator();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed class SplitProbe
    {
        public int VoidCalls { get; set; }
        public int Notifications { get; set; }
    }

    public sealed record SplitEchoRequest(string Value) : IRequest<string>;

    public sealed record SplitVoidRequest : IRequest;

    public sealed record SplitNotification : INotification;

    public sealed class SplitEchoHandler : IRequestHandler<SplitEchoRequest, string>
    {
        public Task<string> Handle(SplitEchoRequest request, CancellationToken cancellationToken)
            => Task.FromResult($"echo:{request.Value}");
    }

    public sealed class SplitVoidHandler(SplitProbe probe) : RequestHandler<SplitVoidRequest>
    {
        protected override Task HandleCore(SplitVoidRequest request, CancellationToken cancellationToken)
        {
            probe.VoidCalls++;
            return Task.CompletedTask;
        }
    }

    public sealed class SplitNotificationHandler(SplitProbe probe) : INotificationHandler<SplitNotification>
    {
        public Task Handle(SplitNotification notification, CancellationToken cancellationToken)
        {
            probe.Notifications++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send(IRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }
}
