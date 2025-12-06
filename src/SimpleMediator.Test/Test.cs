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
}
