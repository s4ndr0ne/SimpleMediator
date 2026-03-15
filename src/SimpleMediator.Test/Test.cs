using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMeediator.Test;

public class UnitTest1
{
    [Fact]
    public async Task Send_ReturnsExpectedResponse_ForRegisteredRequestHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        var request = new PingRequest("hello");

        // Act
        var response = await mediator.Send<string>(request);

        // Assert
        Assert.Equal("PONG: hello", response);
    }

    [Fact]
    public async Task Send_ThrowsInvalidOperationException_WhenNoHandlerRegistered()
    {
        // Arrange
        var provider = new ServiceCollection().BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await mediator.Send<string>(new UnhandledRequest("missing"));
        });

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Publish_InvokesAllNotificationHandlers()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddTransient<INotificationHandler<TestNotification>, FirstNotificationHandler>();
       
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        var notification = new TestNotification("N1");

        // Act
        await mediator.Publish(notification);

        // Assert
        Assert.Equal(1, probe.Count);
        Assert.Contains("First:N1", probe.Events);     
    }

    public class CallProbe
    {
        public int Count { get; private set; }
        public List<string> Events { get; } = new List<string>();
        public void Record(string evt)
        {
            Count++;
            Events.Add(evt);
        }
    }

    [Fact]
    public async Task Send_InvokesPreAndPostHandlers_AroundRequestHandler()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);

        services.AddTransient<IRequestHandler<PrePostRequest, string>, PrePostRequestHandler>();
        services.AddTransient<IPreRequestHandler<PrePostRequest, string>, SamplePreHandler>();
        services.AddTransient<IPostRequestHandler<PrePostRequest, string>, SamplePostHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        var request = new PrePostRequest("abc");

        // Act
        var response = await mediator.Send<string>(request);

        // Assert
        Assert.Equal("Handled: abc", response);
        Assert.Equal(3, probe.Count);
        Assert.Equal("Pre:abc", probe.Events[0]);
        Assert.Equal("Handler:abc", probe.Events[1]);
        Assert.Equal("Post:abc:Handled: abc", probe.Events[2]);
    }

    // Request expecting a string response
    public record PingRequest(string Message) : IRequest<string>;

    public class PingRequestHandler : IRequestHandler<PingRequest, string>
    {
        public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
            => Task.FromResult($"PONG: {request.Message}");
    }

    public record UnhandledRequest(string Payload) : IRequest<string>;

    public record TestNotification(string Name) : INotification;

    public class FirstNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly CallProbe _probe;
        public FirstNotificationHandler(CallProbe probe) => _probe = probe;

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _probe.Record($"First:{notification.Name}");
            return Task.CompletedTask;
        }
    }

    // Pre/Post request test artifacts
    public record PrePostRequest(string Message) : IRequest<string>;

    public class PrePostRequestHandler : IRequestHandler<PrePostRequest, string>
    {
        private readonly CallProbe _probe;
        public PrePostRequestHandler(CallProbe probe) => _probe = probe;

        public Task<string> Handle(PrePostRequest request, CancellationToken cancellationToken)
        {
            _probe.Record($"Handler:{request.Message}");
            return Task.FromResult($"Handled: {request.Message}");
        }
    }

    public class SamplePreHandler : IPreRequestHandler<PrePostRequest, string>
    {
        private readonly CallProbe _probe;
        public SamplePreHandler(CallProbe probe) => _probe = probe;

        public Task Handle(PrePostRequest request, CancellationToken cancellationToken)
        {
            _probe.Record($"Pre:{request.Message}");
            return Task.CompletedTask;
        }
    }

    public class SamplePostHandler : IPostRequestHandler<PrePostRequest, string>
    {
        private readonly CallProbe _probe;
        public SamplePostHandler(CallProbe probe) => _probe = probe;

        public Task Handle(PrePostRequest request, string response, CancellationToken cancellationToken)
        {
            _probe.Record($"Post:{request.Message}:{response}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Send_PropagatesCancellationToken_ToHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<CancellableRequest, string>, CancellableRequestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await mediator.Send<string>(new CancellableRequest(), cts.Token);
        });
    }

    public record CancellableRequest() : IRequest<string>;

    public class CancellableRequestHandler : IRequestHandler<CancellableRequest, string>
    {
        public async Task<string> Handle(CancellableRequest request, CancellationToken cancellationToken)
        {
            // honor cancellation
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return "done";
        }
    }

    [Fact]
    public async Task Send_ResolvesScopedService_PerInvocation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDep>();
        services.AddTransient<IRequestHandler<ScopedRequest, Guid>, ScopedRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        var id1 = await mediator.Send<Guid>(new ScopedRequest());
        var id2 = await mediator.Send<Guid>(new ScopedRequest());

        // Assert - each Send creates its own scope so ScopedDep should differ
        Assert.NotEqual(id1, id2);
    }

    public record ScopedRequest() : IRequest<Guid>;

    public class ScopedDep
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public class ScopedRequestHandler : IRequestHandler<ScopedRequest, Guid>
    {
        private readonly ScopedDep _dep;
        public ScopedRequestHandler(ScopedDep dep) => _dep = dep;

        public Task<Guid> Handle(ScopedRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_dep.Id);
        }
    }

}
