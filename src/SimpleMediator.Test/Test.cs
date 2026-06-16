using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Test;

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

        // Act
        InvalidOperationException? caught = null;
        try
        {
            await mediator.Send<string>(new UnhandledRequest("missing"));
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        // Assert
        Assert.NotNull(caught);
        Assert.Contains("No service for type", caught.Message);
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
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        var result = await mediator.Send<string>(new CancellableRequest(), cts.Token);

        // Assert - handler should detect cancellation and return early
        Assert.Equal("cancelled", result);
    }

    public record CancellableRequest() : IRequest<string>;

    public class CancellableRequestHandler : IRequestHandler<CancellableRequest, string>
    {
        public async Task<string> Handle(CancellableRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return "done";
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
        }
    }

    [Fact]
    public async Task Send_UsesSameScopedService_WithinSameScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ScopedDep>();
        services.AddTransient<IRequestHandler<ScopedRequest, Guid>, ScopedRequestHandler>();

        var provider = services.BuildServiceProvider();
        
        using (var scope = provider.CreateScope())
        {
            var mediator = new Mediator(scope.ServiceProvider);

            // Act
            var id1 = await mediator.Send<Guid>(new ScopedRequest());
            var id2 = await mediator.Send<Guid>(new ScopedRequest());

            // Assert - mediator uses the provided service provider (scope), so dependencies should be the same
            Assert.Equal(id1, id2);
        }
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

    // ---- Pipeline behavior ordering by Order property ----

    [Fact]
    public async Task Behaviors_RunInAscendingOrderOfOrderProperty()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            // Registered out of order on purpose; the Order property decides execution order.
            options.AddBehavior(typeof(SecondBehavior<,>)); // Order = 10
            options.AddBehavior(typeof(FirstBehavior<,>));  // Order = 1
        });
        services.AddTransient<IRequestHandler<OrderedRequest, string>, OrderedRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Act
        await mediator.Send<string>(new OrderedRequest());

        // Assert - lowest Order is outermost (runs first, unwinds last)
        Assert.Equal(
            new[] { "first:before", "second:before", "handler", "second:after", "first:after" },
            probe.Events.ToArray());
    }

    [Fact]
    public async Task ClosedBehavior_IsRegisteredAndInvoked()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            // A concrete, non-open-generic behavior.
            options.AddBehavior(typeof(ClosedOrderedBehavior));
        });
        services.AddTransient<IRequestHandler<OrderedRequest, string>, OrderedRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        // Act
        await mediator.Send<string>(new OrderedRequest());

        // Assert
        Assert.Contains("closed:before", probe.Events);
        Assert.Contains("closed:after", probe.Events);
    }

    [Fact]
    public void AddBehavior_Throws_WhenTypeDoesNotImplementPipelineBehavior()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddSimpleMediator(options => options.AddBehavior(typeof(NotABehavior))));
    }

    public record OrderedRequest() : IRequest<string>;

    public class OrderedRequestHandler : IRequestHandler<OrderedRequest, string>
    {
        private readonly CallProbe _probe;
        public OrderedRequestHandler(CallProbe probe) => _probe = probe;

        public Task<string> Handle(OrderedRequest request, CancellationToken cancellationToken)
        {
            _probe.Record("handler");
            return Task.FromResult("ok");
        }
    }

    public class FirstBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly CallProbe _probe;
        public FirstBehavior(CallProbe probe) => _probe = probe;

        public int Order => 1;

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _probe.Record("first:before");
            var response = await next(cancellationToken);
            _probe.Record("first:after");
            return response;
        }
    }

    public class SecondBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly CallProbe _probe;
        public SecondBehavior(CallProbe probe) => _probe = probe;

        public int Order => 10;

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _probe.Record("second:before");
            var response = await next(cancellationToken);
            _probe.Record("second:after");
            return response;
        }
    }

    public class ClosedOrderedBehavior : IPipelineBehavior<OrderedRequest, string>
    {
        private readonly CallProbe _probe;
        public ClosedOrderedBehavior(CallProbe probe) => _probe = probe;

        public int Order => 0;

        public async Task<string> Handle(OrderedRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _probe.Record("closed:before");
            var response = await next(cancellationToken);
            _probe.Record("closed:after");
            return response;
        }
    }

    public class NotABehavior { }

    // ---- Notifications with multiple handlers ----

    [Fact]
    public async Task Publish_InvokesAllHandlers_WhenMultipleRegistered()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddTransient<INotificationHandler<TestNotification>, FirstNotificationHandler>();
        services.AddTransient<INotificationHandler<TestNotification>, SecondNotificationHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        await mediator.Publish(new TestNotification("N1"));

        // Assert
        Assert.Equal(2, probe.Count);
        Assert.Contains("First:N1", probe.Events);
        Assert.Contains("Second:N1", probe.Events);
    }

    [Fact]
    public async Task Publish_DoesNotThrow_WhenNoHandlersRegistered()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var mediator = new Mediator(provider);

        await mediator.Publish(new TestNotification("none"));
    }

    public class SecondNotificationHandler : INotificationHandler<TestNotification>
    {
        private readonly CallProbe _probe;
        public SecondNotificationHandler(CallProbe probe) => _probe = probe;

        public Task Handle(TestNotification notification, CancellationToken cancellationToken)
        {
            _probe.Record($"Second:{notification.Name}");
            return Task.CompletedTask;
        }
    }

}
