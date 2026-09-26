using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimpleMediator;
using SimpleMediator.Configuration;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Tests;

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
        Assert.Contains("No request handler registered", caught.Message);
    }

    [Fact]
    public async Task Send_ThrowsInvalidOperationException_WhenMultipleHandlersRegisteredForSameRequest()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();
        services.AddTransient<IRequestHandler<PingRequest, string>, DuplicatePingRequestHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        var exception = await Assert.ThrowsAsync<RequestHandlerResolutionException>(() =>
            mediator.Send<string>(new PingRequest("hello")));

        // Assert
        Assert.Contains("Multiple request handlers registered", exception.Message);
    }

    [Fact]
    public async Task Publish_DoesNotDuplicateHandlers_WhenAssemblyRegisteredMultipleTimes()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            var assembly = typeof(UnitTest1).Assembly;
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(assembly);
            options.RegisterAssembly(assembly);
        });

        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act
        await mediator.Publish(new TestNotification("N1"));

        // Assert - FirstNotificationHandler and SecondNotificationHandler should run once each.
        Assert.Equal(2, probe.Count);
        Assert.Equal(1, probe.Events.Count(e => e == "First:N1"));
        Assert.Equal(1, probe.Events.Count(e => e == "Second:N1"));
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

    public class DuplicatePingRequestHandler : IRequestHandler<PingRequest, string>
    {
        public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
            => Task.FromResult($"DUPLICATE: {request.Message}");
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

    [Fact]
    public async Task Send_DisposesManuallyActivatedOpenGenericHandler()
    {
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });

        using var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        await mediator.Send(new DisposableOpenGenericRequest<int>(42));

        Assert.Contains("open-disposed", probe.Events);
    }

    [Fact]
    public async Task Send_DisposesOpenGenericHandlerWhenPipelineResolutionFails()
    {
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        services.AddTransient<IPreRequestHandler<DisposableOpenGenericRequest<int>, int>>(
            _ => throw new InvalidOperationException("Unable to construct pre-handler."));

        using var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new DisposableOpenGenericRequest<int>(42)));

        Assert.Equal("Unable to construct pre-handler.", exception.Message);
        Assert.Contains("open-disposed", probe.Events);
    }

    [Fact]
    public void OpenGenericResolutionCache_IsBounded()
    {
        var configuration = new MediatorConfiguration(
            NotificationPublishStrategy.Sequential,
            new[] { new OpenGenericHandlerRegistration(typeof(EchoHandler<>), ServiceLifetime.Transient) },
            resolutionCacheCapacity: 1);

        var first = configuration.ResolveOpenGeneric(typeof(EchoRequest<int>), typeof(int));
        _ = configuration.ResolveOpenGeneric(typeof(EchoRequest<string>), typeof(string));
        var firstAgain = configuration.ResolveOpenGeneric(typeof(EchoRequest<int>), typeof(int));

        Assert.NotSame(first, firstAgain);
    }

    public record DisposableOpenGenericRequest<T>(T Value) : IRequest<T>;

    public sealed class DisposableOpenGenericHandler<T> : IRequestHandler<DisposableOpenGenericRequest<T>, T>, IDisposable
    {
        private readonly CallProbe _probe;

        public DisposableOpenGenericHandler(CallProbe probe) => _probe = probe;

        public Task<T> Handle(DisposableOpenGenericRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);

        public void Dispose() => _probe.Record("open-disposed");
    }

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
        var mediator = ResolveScopedMediator(provider);

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
        var mediator = ResolveScopedMediator(provider);

        // Act
        await mediator.Send<string>(new OrderedRequest());

        // Assert
        Assert.Contains("closed:before", probe.Events);
        Assert.Contains("closed:after", probe.Events);
    }

    [Fact]
    public async Task Behaviors_WithEqualOrder_PreserveRegistrationOrder()
    {
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.AddBehavior(typeof(EqualFirstBehavior<,>));
            options.AddBehavior(typeof(EqualSecondBehavior<,>));
        });
        services.AddTransient<IRequestHandler<OrderedRequest, string>, OrderedRequestHandler>();

        using var provider = services.BuildServiceProvider();
        await ResolveScopedMediator(provider).Send<string>(new OrderedRequest());

        // v4 contract: first registered behavior with equal Order is outermost and runs first (FIFO).
        Assert.Equal(
            new[] { "equal-first:before", "equal-second:before", "handler", "equal-second:after", "equal-first:after" },
            probe.Events.ToArray());
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

    public class EqualFirstBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly CallProbe _probe;
        public EqualFirstBehavior(CallProbe probe) => _probe = probe;
        public int Order => 0;
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _probe.Record("equal-first:before");
            var response = await next(cancellationToken);
            _probe.Record("equal-first:after");
            return response;
        }
    }

    public class EqualSecondBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly CallProbe _probe;
        public EqualSecondBehavior(CallProbe probe) => _probe = probe;
        public int Order => 0;
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            _probe.Record("equal-second:before");
            var response = await next(cancellationToken);
            _probe.Record("equal-second:after");
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

    // ---- Notification publish strategy (sequential default vs parallel) ----

    [Fact]
    public async Task Publish_IsSequentialByDefault_HandlersDoNotOverlap()
    {
        // Arrange - default strategy (no NotificationPublishStrategy set).
        var probe = new ConcurrencyProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act
        await mediator.Publish(new ConcurrencyNotification());

        // Assert - sequential dispatch never runs two handlers at once.
        Assert.Equal(2, probe.Entered);
        Assert.Equal(1, probe.MaxObserved);
    }

    [Fact]
    public async Task Publish_RunsInParallel_WhenParallelStrategyConfigured()
    {
        // Arrange
        var probe = new ConcurrencyProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act
        await mediator.Publish(new ConcurrencyNotification());

        // Assert - both handlers were in-flight simultaneously.
        Assert.Equal(2, probe.Entered);
        Assert.Equal(2, probe.MaxObserved);
    }

    [Fact]
    public async Task Publish_Parallel_AggregatesAllHandlerExceptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act - two handlers both throw.
        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            mediator.Publish(new FailingNotification()));

        // Assert - every failure is surfaced, not just the first.
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, e => Assert.IsType<InvalidOperationException>(e));
    }

    [Fact]
    public async Task Publish_Parallel_PreservesSingleHandlerException()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
        });
        services.AddTransient<INotificationHandler<SingleFailureNotification>, SingleFailureHandler>();
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Publish(new SingleFailureNotification()));

        Assert.Equal("single failure", exception.Message);
    }

    [Fact]
    public async Task Publish_Parallel_PreservesSingleNullTaskException()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
        });
        services.AddTransient<INotificationHandler<NullTaskNotification>, NullTaskHandler>();
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Publish(new NullTaskNotification()));

        Assert.Contains("returned a null Task", exception.Message);
    }

    [Fact]
    public async Task Publish_Parallel_AggregatesCancellationAndFailure()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
        });
        services.AddTransient<INotificationHandler<MixedFailureNotification>, MixedCancellationHandler>();
        services.AddTransient<INotificationHandler<MixedFailureNotification>, MixedErrorHandler>();
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => mediator.Publish(new MixedFailureNotification(), cancellation.Token));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains(exception.InnerExceptions, error => error is OperationCanceledException);
        Assert.Contains(exception.InnerExceptions, error => error is InvalidOperationException);
    }

    [Fact]
    public async Task Publish_Sequential_StopsAfterFirstException()
    {
        // Arrange - manual registration so dispatch order is deterministic
        // (throwing handler first). new Mediator(provider) => default Sequential.
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddTransient<INotificationHandler<FailFastNotification>, ThrowingFailFastHandler>();
        services.AddTransient<INotificationHandler<FailFastNotification>, RecordingFailFastHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Publish(new FailFastNotification()));

        // Assert - the second handler never ran.
        Assert.Empty(probe.Events);
    }

    [Fact]
    public void Mediator_IsRegisteredAsTransient_ResolvesFreshInstancePerRequest()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var provider = services.BuildServiceProvider();

        // Act - resolve twice from the SAME (root) provider.
        var first = ResolveScopedMediator(provider);
        var second = ResolveScopedMediator(provider);

        // Assert - Transient yields distinct instances (Scoped/Singleton would not).
        Assert.NotSame(first, second);
    }

    public record ConcurrencyNotification() : INotification;

    // Tracks the maximum number of handlers running at the same time.
    public class ConcurrencyProbe
    {
        private int _current;
        private int _max;
        private int _entered;
        public int MaxObserved => Volatile.Read(ref _max);
        public int Entered => Volatile.Read(ref _entered);

        public async Task EnterAndWait(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entered);
            var now = Interlocked.Increment(ref _current);
            UpdateMax(now);
            try
            {
                await Task.Delay(80, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private void UpdateMax(int candidate)
        {
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref _max);
                if (candidate <= snapshot) return;
            }
            while (Interlocked.CompareExchange(ref _max, candidate, snapshot) != snapshot);
        }
    }

    public class FirstConcurrencyHandler : INotificationHandler<ConcurrencyNotification>
    {
        private readonly ConcurrencyProbe _probe;
        public FirstConcurrencyHandler(ConcurrencyProbe probe) => _probe = probe;
        public Task Handle(ConcurrencyNotification notification, CancellationToken cancellationToken)
            => _probe.EnterAndWait(cancellationToken);
    }

    public class SecondConcurrencyHandler : INotificationHandler<ConcurrencyNotification>
    {
        private readonly ConcurrencyProbe _probe;
        public SecondConcurrencyHandler(ConcurrencyProbe probe) => _probe = probe;
        public Task Handle(ConcurrencyNotification notification, CancellationToken cancellationToken)
            => _probe.EnterAndWait(cancellationToken);
    }

    public record FailingNotification() : INotification;

    public class FirstFailingHandler : INotificationHandler<FailingNotification>
    {
        public Task Handle(FailingNotification notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("first failed");
    }

    public class SecondFailingHandler : INotificationHandler<FailingNotification>
    {
        public Task Handle(FailingNotification notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("second failed");
    }

    public record SingleFailureNotification() : INotification;

    public class SingleFailureHandler : INotificationHandler<SingleFailureNotification>
    {
        public Task Handle(SingleFailureNotification notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("single failure");
    }

    public record NullTaskNotification() : INotification;

    public class NullTaskHandler : INotificationHandler<NullTaskNotification>
    {
        public Task Handle(NullTaskNotification notification, CancellationToken cancellationToken)
            => null!;
    }

    public record MixedFailureNotification() : INotification;

    public class MixedCancellationHandler : INotificationHandler<MixedFailureNotification>
    {
        public Task Handle(MixedFailureNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new OperationCanceledException());
    }

    public class MixedErrorHandler : INotificationHandler<MixedFailureNotification>
    {
        public Task Handle(MixedFailureNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("mixed failure"));
    }

    public record FailFastNotification() : INotification;

    public class ThrowingFailFastHandler : INotificationHandler<FailFastNotification>
    {
        public Task Handle(FailFastNotification notification, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    public class RecordingFailFastHandler : INotificationHandler<FailFastNotification>
    {
        private readonly CallProbe _probe;
        public RecordingFailFastHandler(CallProbe probe) => _probe = probe;
        public Task Handle(FailFastNotification notification, CancellationToken cancellationToken)
        {
            _probe.Record("second-ran");
            return Task.CompletedTask;
        }
    }

    // ---- Open-generic request handlers (request type is itself generic) ----

    [Fact]
    public async Task Send_ResolvesOpenGenericHandler_ForDifferentClosedRequestTypes()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act - the SAME open-generic handler serves both closed request types.
        var asInt = await mediator.Send<int>(new EchoRequest<int>(42));
        var asString = await mediator.Send<string>(new EchoRequest<string>("hi"));

        // Assert
        Assert.Equal(42, asInt);
        Assert.Equal("hi", asString);
    }

    [Fact]
    public async Task Send_OpenGenericHandler_GetsConstructorDependenciesInjected()
    {
        // Arrange
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act
        var result = await mediator.Send<string>(new WrapRequest<int>(7));

        // Assert - handler ran with its injected dependency and saw the closed type arg.
        Assert.Equal("[Int32] 7", result);
        Assert.Contains("wrap:7", probe.Events);
    }

    [Fact]
    public async Task Send_CustomOpenGenericHandler_RespectsScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Scoped;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });

        using var provider = services.BuildServiceProvider();
        Guid firstScopeId;
        Guid secondScopeId;

        using (var scope = provider.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            firstScopeId = await mediator.Send<Guid>(new ScopedGenericRequest<int>());
            var sameScopeId = await mediator.Send<Guid>(new ScopedGenericRequest<int>());
            Assert.Equal(firstScopeId, sameScopeId);
        }

        using (var scope = provider.CreateScope())
        {
            secondScopeId = await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send<Guid>(new ScopedGenericRequest<int>());
        }

        Assert.NotEqual(firstScopeId, secondScopeId);
    }

    [Fact]
    public async Task Send_CustomOpenGenericHandler_RespectsSingletonLifetime()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Singleton;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });

        using var provider = services.BuildServiceProvider();
        Guid firstId;
        using (var scope = provider.CreateScope())
        {
            firstId = await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send<Guid>(new SingletonGenericRequest<int>());
        }

        using (var scope = provider.CreateScope())
        {
            var secondId = await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send<Guid>(new SingletonGenericRequest<int>());
            Assert.Equal(firstId, secondId);
        }
    }

    [Fact]
    public async Task Send_Throws_WhenClosedAndOpenGenericHandlersBothMatch()
    {
        // Arrange - a closed handler and an open-generic handler both satisfy AmbiguousRequest<int>.
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<RequestHandlerResolutionException>(() =>
            mediator.Send<int>(new AmbiguousRequest<int>(1)));
        Assert.Contains("Multiple request handlers registered", ex.Message);
    }

    public record EchoRequest<T>(T Value) : IRequest<T>;

    public class EchoHandler<T> : IRequestHandler<EchoRequest<T>, T>
    {
        public Task<T> Handle(EchoRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);
    }

    public record WrapRequest<T>(T Value) : IRequest<string>;

    public class WrapHandler<T> : IRequestHandler<WrapRequest<T>, string>
    {
        private readonly CallProbe _probe;
        public WrapHandler(CallProbe probe) => _probe = probe;

        public Task<string> Handle(WrapRequest<T> request, CancellationToken cancellationToken)
        {
            _probe.Record($"wrap:{request.Value}");
            return Task.FromResult($"[{typeof(T).Name}] {request.Value}");
        }
    }

    public record AmbiguousRequest<T>(T Value) : IRequest<T>;

    public class OpenAmbiguousHandler<T> : IRequestHandler<AmbiguousRequest<T>, T>
    {
        public Task<T> Handle(AmbiguousRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);
    }

    public class ClosedAmbiguousHandler : IRequestHandler<AmbiguousRequest<int>, int>
    {
        public Task<int> Handle(AmbiguousRequest<int> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);
    }

    // ---- Exception-handling pipeline ----

    [Fact]
    public async Task Send_ExceptionHandler_RecoversWithSubstituteResponse()
    {
        // Arrange - manual registration keeps the test isolated from assembly scanning.
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RecoverableRequest, string>, ThrowingRequestHandler>();
        services.AddTransient<IRequestExceptionHandler<RecoverableRequest, string>, RecoveringExceptionHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        var result = await mediator.Send<string>(new RecoverableRequest("x"));

        // Assert - the thrown exception was swallowed and replaced by the substitute.
        Assert.Equal("recovered: kaboom", result);
    }

    [Fact]
    public async Task Send_OpenGenericExceptionHandler_RunsButRethrows_WhenNotHandled()
    {
        // Arrange - a catch-all open-generic exception handler (registered the MS.DI-native way).
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddTransient<IRequestHandler<RecoverableRequest, string>, ThrowingRequestHandler>();
        services.AddTransient(typeof(IRequestExceptionHandler<,>), typeof(LoggingExceptionHandler<,>));
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act + Assert - handler observed the exception but did not handle it, so it propagates.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.Send<string>(new RecoverableRequest("y")));
        Assert.Equal("kaboom", ex.Message);
        Assert.Contains("logged:kaboom", probe.Events);
    }

    public record RecoverableRequest(string Message) : IRequest<string>;

    public class ThrowingRequestHandler : IRequestHandler<RecoverableRequest, string>
    {
        public Task<string> Handle(RecoverableRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("kaboom");
    }

    public class RecoveringExceptionHandler : IRequestExceptionHandler<RecoverableRequest, string>
    {
        public Task Handle(RecoverableRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled($"recovered: {exception.Message}");
            return Task.CompletedTask;
        }
    }

    public class LoggingExceptionHandler<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly CallProbe _probe;
        public LoggingExceptionHandler(CallProbe probe) => _probe = probe;

        public Task Handle(TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken)
        {
            _probe.Record($"logged:{exception.Message}");
            return Task.CompletedTask; // intentionally does not SetHandled
        }
    }

    // ---- Cancellation semantics ----

    [Fact]
    public async Task Send_PropagatesCancellation_AndExceptionHandlerDoesNotSwallowIt()
    {
        // Arrange - a greedy exception handler would "recover" any exception, but a genuine
        // cancellation must still propagate.
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<CancelDuringRequest, string>, CancelDuringHandler>();
        services.AddTransient<IRequestExceptionHandler<CancelDuringRequest, string>, GreedyExceptionHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Act + Assert - cancellation wins over the exception handler.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mediator.Send<string>(new CancelDuringRequest(), cts.Token));
    }

    public record CancelDuringRequest() : IRequest<string>;

    public class CancelDuringHandler : IRequestHandler<CancelDuringRequest, string>
    {
        public async Task<string> Handle(CancelDuringRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "done";
        }
    }

    public class GreedyExceptionHandler : IRequestExceptionHandler<CancelDuringRequest, string>
    {
        public Task Handle(CancelDuringRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("recovered");
            return Task.CompletedTask;
        }
    }

    // ---- Exception handler ordering ----

    [Fact]
    public async Task ExceptionHandlers_RunInAscendingOrder_RegardlessOfRegistrationOrder()
    {
        // Arrange - register high-Order first; the lower-Order handler must still run first.
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<OrderedFailRequest, string>, OrderedFailHandler>();
        services.AddTransient<IRequestExceptionHandler<OrderedFailRequest, string>, HighOrderExceptionHandler>();
        services.AddTransient<IRequestExceptionHandler<OrderedFailRequest, string>, LowOrderExceptionHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        // Act
        var result = await mediator.Send<string>(new OrderedFailRequest());

        // Assert - the lower-Order handler handled it first.
        Assert.Equal("low", result);
    }

    public record OrderedFailRequest() : IRequest<string>;

    public class OrderedFailHandler : IRequestHandler<OrderedFailRequest, string>
    {
        public Task<string> Handle(OrderedFailRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("fail");
    }

    public class HighOrderExceptionHandler : IRequestExceptionHandler<OrderedFailRequest, string>
    {
        public int Order => 10;
        public Task Handle(OrderedFailRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("high");
            return Task.CompletedTask;
        }
    }

    public class LowOrderExceptionHandler : IRequestExceptionHandler<OrderedFailRequest, string>
    {
        public int Order => 1;
        public Task Handle(OrderedFailRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("low");
            return Task.CompletedTask;
        }
    }

    // ---- Startup validation ----

    [Fact]
    public void ValidateSimpleMediator_Throws_OnDuplicateClosedHandlers()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();
        services.AddTransient<IRequestHandler<PingRequest, string>, DuplicatePingRequestHandler>();

        var ex = Assert.Throws<RequestHandlerResolutionException>(() => services.ValidateSimpleMediator());
        Assert.Contains("Multiple request handlers registered", ex.Message);
    }

    [Fact]
    public void ValidateSimpleMediator_Throws_OnClosedAndOpenGenericAmbiguity()
    {
        // Isolated scenario: one closed handler plus an open-generic handler that also matches it.
        // (Built directly via internals so the shared test assembly's other types don't interfere.)
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<AmbiguousRequest<int>, int>, ClosedAmbiguousHandler>();
        services.AddSingleton(new MediatorConfiguration(
            NotificationPublishStrategy.Sequential,
            new[] { new OpenGenericHandlerRegistration(typeof(OpenAmbiguousHandler<>), ServiceLifetime.Transient) }));

        var ex = Assert.Throws<RequestHandlerResolutionException>(() => services.ValidateSimpleMediator());
        Assert.Contains("open-generic handler", ex.Message);
    }

    [Fact]
    public void ValidateOnBuild_FailsFast_DuringAddSimpleMediator()
    {
        var ex = Assert.Throws<RequestHandlerResolutionException>(() =>
            new ServiceCollection().AddSimpleMediator(options =>
            {
                options.RegisterAssembly(typeof(UnitTest1).Assembly);
                options.ValidateOnBuild = true;
            }));

        Assert.Contains("handler", ex.Message);
    }

    [Fact]
    public void ValidateSimpleMediator_Throws_OnFactoryAndInstanceDuplicates()
    {
        // Two registrations for the same closed handler, neither using an implementation type.
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<PingRequest, string>>(_ => new PingRequestHandler());
        services.AddSingleton<IRequestHandler<PingRequest, string>>(new PingRequestHandler());

        var ex = Assert.Throws<RequestHandlerResolutionException>(() => services.ValidateSimpleMediator());
        Assert.Contains("Multiple request handlers registered", ex.Message);
    }

    [Fact]
    public void ValidateSimpleMediator_RejectsOpenGenericServiceMapping()
    {
        var services = new ServiceCollection();

        // OpenGenericPreHandler<,> implements the pre-handler contract, not the notification
        // contract, so Microsoft DI cannot close it for INotificationHandler<>.
        services.AddTransient(typeof(INotificationHandler<>), typeof(OpenGenericPreHandler<,>));

        var ex = Assert.Throws<InvalidOperationException>(() => services.ValidateSimpleMediator());
        Assert.Contains("does not implement", ex.Message);
    }

    [Fact]
    public void AddSimpleMediator_RejectsOpenGenericServiceMappingWithoutOptIn()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(INotificationHandler<>), typeof(OpenGenericPreHandler<,>));

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSimpleMediator());

        Assert.Contains("does not implement", ex.Message);
    }

    [Fact]
    public void ValidateSimpleMediator_RejectsMismatchedClosedImplementation()
    {
        var services = new ServiceCollection();
        services.AddTransient(
            typeof(IRequestHandler<PrePostRequest, string>),
            typeof(SamplePreHandler));

        var ex = Assert.Throws<InvalidOperationException>(() => services.ValidateSimpleMediator());
        Assert.Contains("does not implement service", ex.Message);
    }

    [Fact]
    public void ValidateSimpleMediator_RejectsUninferableOpenGenericRequestHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MediatorConfiguration(
            NotificationPublishStrategy.Sequential,
            new[] { new OpenGenericHandlerRegistration(typeof(OpenGenericPreHandler<,>), ServiceLifetime.Transient) }));

        var ex = Assert.Throws<InvalidOperationException>(() => services.ValidateSimpleMediator());
        Assert.Contains("cannot be inferred", ex.Message);
    }

    [Fact]
    public void AddSimpleMediator_RejectsOpenGenericRequestHandlerWithoutPublicConstructor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new MediatorConfiguration(
            NotificationPublishStrategy.Sequential,
            new[] { new OpenGenericHandlerRegistration(CreatePrivateConstructorOpenGenericHandler(), ServiceLifetime.Transient) }));

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddSimpleMediator());

        Assert.Contains("must expose a public constructor", ex.Message);
    }

    [Fact]
    public void AddBehavior_RejectsAbstractBehaviorImmediately()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddSimpleMediator(options => options.AddBehavior(typeof(AbstractBehavior<,>))));

        Assert.Contains("must be a concrete", ex.Message);
    }

    // ---- Modular registration: repeated AddSimpleMediator calls ----

    [Fact]
    public async Task AddSimpleMediator_CalledTwice_AccumulatesOpenGenericHandlers()
    {
        var services = new ServiceCollection();
        // First "module": an assembly that contributes no open-generic handlers.
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(IMediator).Assembly));
        // Second "module": brings EchoHandler<>. Under the old TryAdd-first-wins behaviour the
        // config from the first call would win and this handler would be lost.
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));

        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        var result = await mediator.Send<int>(new EchoRequest<int>(99));
        Assert.Equal(99, result);
    }

    [Fact]
    public void ValidationRequested_PersistsAcrossModularRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.ValidateOnBuild = true);
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();
        services.AddTransient<IRequestHandler<PingRequest, string>, DuplicatePingRequestHandler>();

        var ex = Assert.Throws<RequestHandlerResolutionException>(() => services.AddSimpleMediator());

        Assert.Contains("Multiple request handlers registered", ex.Message);
    }

    [Fact]
    public void ExplicitValidation_PersistsAcrossModularRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        services.ValidateSimpleMediator();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();
        services.AddTransient<IRequestHandler<PingRequest, string>, DuplicatePingRequestHandler>();

        var ex = Assert.Throws<RequestHandlerResolutionException>(() => services.AddSimpleMediator());

        Assert.Contains("Multiple request handlers registered", ex.Message);
    }

    // ---- Open-generic handler with an array response ----

    [Fact]
    public async Task Send_ResolvesOpenGenericHandler_WithArrayResponse()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        var result = await mediator.Send<int[]>(new ArrayEchoRequest<int>(new[] { 1, 2, 3 }));

        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    public record ArrayEchoRequest<T>(T[] Values) : IRequest<T[]>;

    public record ScopedGenericRequest<T>() : IRequest<Guid>;

    public sealed class ScopedGenericHandler<T> : IRequestHandler<ScopedGenericRequest<T>, Guid>
    {
        private readonly Guid _id = Guid.NewGuid();

        public Task<Guid> Handle(ScopedGenericRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(_id);
    }

    public record SingletonGenericRequest<T>() : IRequest<Guid>;

    public sealed class SingletonGenericHandler<T> : IRequestHandler<SingletonGenericRequest<T>, Guid>
    {
        private readonly Guid _id = Guid.NewGuid();

        public Task<Guid> Handle(SingletonGenericRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(_id);
    }

    public class ArrayEchoHandler<T> : IRequestHandler<ArrayEchoRequest<T>, T[]>
    {
        public Task<T[]> Handle(ArrayEchoRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Values);
    }

    // ---- Open-generic notification and pre/post handlers discovered by scanning ----

    [Fact]
    public async Task Publish_InvokesOpenGenericNotificationHandler_FromAssemblyScan()
    {
        OpenGenericHookProbe.Reset();
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        await mediator.Publish(new OpenGenericObservedNotification("scan"));

        Assert.Equal(new[] { "notification:scan" }, OpenGenericHookProbe.Events.ToArray());
    }

    [Fact]
    public async Task Send_InvokesOpenGenericPreAndPostHandlers_FromAssemblyScan()
    {
        OpenGenericHookProbe.Reset();
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        var response = await mediator.Send<string>(new OpenGenericHookRequest("scan"));

        Assert.Equal("handled:scan", response);
        Assert.Equal(new[] { "pre:scan", "post:scan:handled:scan" }, OpenGenericHookProbe.Events.ToArray());
    }

    public record OpenGenericObservedNotification(string Message) : INotification;

    public record OpenGenericHookRequest(string Message) : IRequest<string>;

    public class OpenGenericHookRequestHandler : IRequestHandler<OpenGenericHookRequest, string>
    {
        public Task<string> Handle(OpenGenericHookRequest request, CancellationToken cancellationToken)
            => Task.FromResult($"handled:{request.Message}");
    }

    public class OpenGenericNotificationHandler<TNotification> : INotificationHandler<TNotification>
        where TNotification : INotification
    {
        public Task Handle(TNotification notification, CancellationToken cancellationToken)
        {
            if (notification is OpenGenericObservedNotification observed)
            {
                OpenGenericHookProbe.Record($"notification:{observed.Message}");
            }

            return Task.CompletedTask;
        }
    }

    public class OpenGenericPreHandler<TRequest, TResponse> : IPreRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task Handle(TRequest request, CancellationToken cancellationToken)
        {
            if (request is OpenGenericHookRequest hook)
            {
                OpenGenericHookProbe.Record($"pre:{hook.Message}");
            }

            return Task.CompletedTask;
        }
    }

    private static (Assembly Assembly, Type HandlerType) CreateNativeOpenGenericHandlerAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"NativeOpenGenericHandler_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Handlers");
        var typeBuilder = module.DefineType(
            "NativeOpenGenericHandler",
            TypeAttributes.Public | TypeAttributes.Class);
        var typeParameters = typeBuilder.DefineGenericParameters("TRequest", "TResponse");
        typeParameters[0].SetInterfaceConstraints(typeof(IRequest<>).MakeGenericType(typeParameters[1]));
        var handlerInterface = typeof(IRequestHandler<,>).MakeGenericType(typeParameters);
        typeBuilder.AddInterfaceImplementation(handlerInterface);
        typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

        var interfaceMethod = typeof(IRequestHandler<,>).GetMethod("Handle")!;
        var implementationMethod = typeBuilder.DefineMethod(
            interfaceMethod.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(Task<>).MakeGenericType(typeParameters[1]),
            new[] { typeParameters[0], typeof(CancellationToken) });
        var generator = implementationMethod.GetILGenerator();
        var responseLocal = generator.DeclareLocal(typeParameters[1]);
        generator.Emit(OpCodes.Ldloca_S, responseLocal);
        generator.Emit(OpCodes.Initobj, typeParameters[1]);
        generator.Emit(OpCodes.Ldloc_0);
        var fromResult = typeof(Task)
            .GetMethod("FromResult")!
            .MakeGenericMethod(typeParameters[1]);
        generator.Emit(OpCodes.Call, fromResult);
        generator.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(
            implementationMethod,
            TypeBuilder.GetMethod(handlerInterface, interfaceMethod));

        var handlerType = typeBuilder.CreateType()!;
        return (assembly, handlerType);
    }

    private static Type CreatePrivateConstructorOpenGenericHandler()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"PrivateConstructorHandler_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Handlers");
        var typeBuilder = module.DefineType(
            "PrivateConstructorOpenGenericHandler",
            TypeAttributes.Public | TypeAttributes.Class);
        var typeParameter = typeBuilder.DefineGenericParameters("T")[0];
        var requestType = typeof(PrivateConstructorRequest<>).MakeGenericType(typeParameter);
        var handlerInterface = typeof(IRequestHandler<,>).MakeGenericType(requestType, typeParameter);
        typeBuilder.AddInterfaceImplementation(handlerInterface);

        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Private,
            CallingConventions.Standard,
            Type.EmptyTypes);
        constructor.GetILGenerator().Emit(OpCodes.Ret);

        var interfaceMethod = typeof(IRequestHandler<,>).GetMethod("Handle")!;
        var implementationMethod = typeBuilder.DefineMethod(
            interfaceMethod.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
            MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(Task<>).MakeGenericType(typeParameter),
            new[] { requestType, typeof(CancellationToken) });
        implementationMethod.GetILGenerator().Emit(OpCodes.Ldnull);
        implementationMethod.GetILGenerator().Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(
            implementationMethod,
            TypeBuilder.GetMethod(handlerInterface, interfaceMethod));

        return typeBuilder.CreateType()!;
    }

    public sealed record PrivateConstructorRequest<T>() : IRequest<T>;

    public class OpenGenericPostHandler<TRequest, TResponse> : IPostRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task Handle(TRequest request, TResponse response, CancellationToken cancellationToken)
        {
            if (request is OpenGenericHookRequest hook)
            {
                OpenGenericHookProbe.Record($"post:{hook.Message}:{response}");
            }

            return Task.CompletedTask;
        }
    }

    private static class OpenGenericHookProbe
    {
        private static readonly object Gate = new();
        private static readonly List<string> RecordedEvents = new();

        public static IReadOnlyList<string> Events
        {
            get
            {
                lock (Gate)
                {
                    return RecordedEvents.ToArray();
                }
            }
        }

        public static void Record(string evt)
        {
            lock (Gate)
            {
                RecordedEvents.Add(evt);
            }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                RecordedEvents.Clear();
            }
        }
    }

    // ---- Cancellation semantics in Publish(Parallel) ----

    [Fact]
    public async Task Publish_Parallel_PropagatesCancellation_AsOperationCanceledException()
    {
        // Arrange - both handlers observe the supplied token, which we cancel. Without the
        // cancellation-aware path they would surface as an AggregateException wrapping two
        // OperationCanceledExceptions — callers expecting cancellation would not see OCE.
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert - the caller sees OperationCanceledException (not AggregateException).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mediator.Publish(new ParallelCancellationNotification(), cts.Token));
    }

    [Fact]
    public async Task Publish_Parallel_PropagatesUncancelledOperationCanceledException()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });
        using var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            mediator.Publish(new ParallelUncancelledNotification()));

        Assert.IsType<OperationCanceledException>(exception);
    }

    public record ParallelCancellationNotification() : INotification;

    public class ParallelCancelHandler1 : INotificationHandler<ParallelCancellationNotification>
    {
        public Task Handle(ParallelCancellationNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new OperationCanceledException(cancellationToken));
    }

    public class ParallelCancelHandler2 : INotificationHandler<ParallelCancellationNotification>
    {
        public Task Handle(ParallelCancellationNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new OperationCanceledException(cancellationToken));
    }

    public record ParallelUncancelledNotification() : INotification;

    public class ParallelUncancelledHandler : INotificationHandler<ParallelUncancelledNotification>
    {
        public Task Handle(ParallelUncancelledNotification notification, CancellationToken cancellationToken)
            => Task.FromException(new OperationCanceledException());
    }

    // ---- Cancellation from an alien token is not swallowed by IRequestExceptionHandler ----

    [Fact]
    public async Task Send_PropagatesOperationCanceledException_FromAlienToken_NotSwallowedByExceptionHandler()
    {
        // Arrange - the user's token is NOT cancelled, but the handler internally observes
        // a separate (alien) token that is cancelled. A greedy exception handler must not
        // swallow that OperationCanceledException and substitute a "recovered" response.
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<AlienCancelRequest, string>, AlienCancelHandler>();
        services.AddTransient<IRequestExceptionHandler<AlienCancelRequest, string>, GreedyAlienExceptionHandler>();
        var provider = services.BuildServiceProvider();
        var mediator = new Mediator(provider);

        using var userCts = new CancellationTokenSource(); // not cancelled
        using var alienCts = new CancellationTokenSource();
        alienCts.Cancel(); // pre-cancelled, before the call

        // Act + Assert - cancellation from the alien token propagates to the caller.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mediator.Send<string>(new AlienCancelRequest(), userCts.Token));
    }

    public record AlienCancelRequest() : IRequest<string>;

    public class AlienCancelHandler : IRequestHandler<AlienCancelRequest, string>
    {
        public Task<string> Handle(AlienCancelRequest request, CancellationToken cancellationToken)
            => Task.FromException<string>(new OperationCanceledException());
    }

    public class GreedyAlienExceptionHandler : IRequestExceptionHandler<AlienCancelRequest, string>
    {
        public Task Handle(AlienCancelRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
        {
            state.SetHandled("recovered"); // must NOT be returned for an OCE
            return Task.CompletedTask;
        }
    }

    // ---- Parameterless AddSimpleMediator overload ----

    [Fact]
    public async Task AddSimpleMediator_Parameterless_RegistersMediator_AndResolvesHandlersAddedManually()
    {
        // Arrange - the convenience overload should still register IMediator, so handlers
        // added explicitly afterwards resolve correctly with no assembly scan.
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        services.AddTransient<IRequestHandler<PingRequest, string>, PingRequestHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act
        var response = await mediator.Send<string>(new PingRequest("plain"));

        // Assert
        Assert.Equal("PONG: plain", response);
    }

    [Fact]
    public void AddSimpleMediator_Parameterless_RegistersMediatorAsTransient()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        var provider = services.BuildServiceProvider();

        var first = ResolveScopedMediator(provider);
        var second = ResolveScopedMediator(provider);

        Assert.NotSame(first, second);
    }

    // ---- RequestHandler<TRequest> convenience base class for void requests ----

    [Fact]
    public async Task Send_VoidRequest_UsesRequestHandlerBaseClass_ToReturnUnit()
    {
        // Arrange - the base class lets users implement void handlers without returning
        // Task<Unit> themselves.
        var probe = new CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator();
        services.AddTransient<IRequestHandler<VoidProbeRequest, Unit>, VoidProbeHandler>();

        var provider = services.BuildServiceProvider();
        var mediator = ResolveScopedMediator(provider);

        // Act - Send(IRequest) does not return a value; it must not throw and the handler
        // must have run exactly once.
        await mediator.Send(new VoidProbeRequest("ping"));

        // Assert
        Assert.Equal(new[] { "void:ping" }, probe.Events.ToArray());
    }

    public record VoidProbeRequest(string Message) : IRequest;

    public class VoidProbeHandler : RequestHandler<VoidProbeRequest>
    {
        private readonly CallProbe _probe;
        public VoidProbeHandler(CallProbe probe) => _probe = probe;

        protected override Task HandleCore(VoidProbeRequest request, CancellationToken cancellationToken)
        {
            _probe.Record($"void:{request.Message}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Send_VoidRequest_PropagatesSynchronouslyCancelledTask()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        services.AddTransient<IRequestHandler<CancelledVoidRequest, Unit>, CancelledVoidHandler>();
        var mediator = ResolveScopedMediator(services.BuildServiceProvider());

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mediator.Send(new CancelledVoidRequest(), cancellationSource.Token));
    }

    public record CancelledVoidRequest : IRequest;

    public class CancelledVoidHandler : RequestHandler<CancelledVoidRequest>
    {
        protected override Task HandleCore(CancelledVoidRequest request, CancellationToken cancellationToken)
            => Task.FromCanceled(cancellationToken);
    }

    // ---- AddBehavior rejects open-generic behaviors that can't be closed ----

    [Fact]
    public void AddBehavior_RejectsMisalignedOpenGenericBehaviorImmediately()
    {
        // A behavior whose type parameters do not line up 1:1 with
        // IPipelineBehavior<TRequest, TResponse> cannot be closed by Microsoft DI.
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddSimpleMediator(options =>
                options.AddBehavior(typeof(MisalignedBehavior<,>))));

        Assert.Contains("cannot be closed", ex.Message);
    }

    /// <summary>
    /// An open-generic behavior whose parameters are declared in a different order
    /// than IPipelineBehavior&lt;TRequest, TResponse&gt;. The 1:1 arity check passes but
    /// the parameter identity check should fail, surfacing the misconfiguration at startup.
    /// </summary>
    public class MisalignedBehavior<TResponse, TRequest> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => 0;
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
            => next(cancellationToken);
    }

    public abstract class AbstractBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => 0;

        public abstract Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken cancellationToken);
    }

    [Fact]
    public void AssemblyScan_RegistersEverySupportedInterface_OnMultiInterfaceOpenGenericHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPreRequestHandler<,>) &&
            descriptor.ImplementationType == typeof(MultiInterfaceOpenGenericHandler<,>));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPostRequestHandler<,>) &&
            descriptor.ImplementationType == typeof(MultiInterfaceOpenGenericHandler<,>));
    }

    [Fact]
    public void AssemblyScan_UsesStableTypeNameOrder()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(UnitTest1).Assembly));

        var handlers = services
            .Where(descriptor => descriptor.ServiceType == typeof(INotificationHandler<TestNotification>))
            .Select(descriptor => descriptor.ImplementationType)
            .ToArray();

        Assert.Equal(
            new[] { typeof(FirstNotificationHandler), typeof(SecondNotificationHandler) },
            handlers);
    }

    [Fact]
    public async Task AssemblyScan_RegistersNativeCompatibleOpenGenericRequestHandlersWithConfiguredLifetime()
    {
        var (assembly, handlerType) = CreateNativeOpenGenericHandlerAssembly();
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Scoped;
            options.RegisterAssembly(assembly);
        });

        var descriptor = Assert.Single(services,
            service => service.ServiceType == typeof(IRequestHandler<,>) &&
                       service.ImplementationType == handlerType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        var configuration = Assert.IsType<MediatorConfiguration>(services
            .Single(service => service.ServiceType == typeof(MediatorConfiguration))
            .ImplementationInstance);
        Assert.Empty(configuration.CustomOpenGenericRequestHandlers);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = provider.CreateScope();
        var firstHandler = firstScope.ServiceProvider.GetRequiredService<IRequestHandler<PingRequest, string>>();
        var sameScopeHandler = firstScope.ServiceProvider.GetRequiredService<IRequestHandler<PingRequest, string>>();

        using var secondScope = provider.CreateScope();
        var secondHandler = secondScope.ServiceProvider.GetRequiredService<IRequestHandler<PingRequest, string>>();

        Assert.Same(firstHandler, sameScopeHandler);
        Assert.NotSame(firstHandler, secondHandler);

        var mediator = firstScope.ServiceProvider.GetRequiredService<IMediator>();
        var response = await mediator.Send<string>(new PingRequest("native"));

        Assert.Null(response);
    }

    [Fact]
    public async Task Send_AggregatesOriginalAndExceptionHandlerFailures()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RecoverableRequest, string>, ThrowingRequestHandler>();
        services.AddTransient<IRequestExceptionHandler<RecoverableRequest, string>, ThrowingExceptionHandler>();
        var mediator = new Mediator(services.BuildServiceProvider());

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            mediator.Send<string>(new RecoverableRequest("x")));

        Assert.Collection(
            exception.InnerExceptions,
            original => Assert.Equal("kaboom", original.Message),
            handler => Assert.Equal("exception handler failed", handler.Message));
    }

    [Fact]
    public void AddSimpleMediator_RejectsInvalidOptionValues()
    {
        var lifetimeException = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddSimpleMediator(options => options.DefaultLifetime = (ServiceLifetime)99));
        Assert.Contains("DefaultLifetime", lifetimeException.Message);

        var strategyException = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddSimpleMediator(options =>
                options.NotificationPublishStrategy = (NotificationPublishStrategy)99));
        Assert.Contains("NotificationPublishStrategy", strategyException.Message);
    }

    public class MultiInterfaceOpenGenericHandler<TRequest, TResponse> :
        IPreRequestHandler<TRequest, TResponse>,
        IPostRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public Task Handle(TRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task Handle(TRequest request, TResponse response, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public class ThrowingExceptionHandler : IRequestExceptionHandler<RecoverableRequest, string>
    {
        public Task Handle(RecoverableRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
            => throw new InvalidOperationException("exception handler failed");
    }

    [Fact]
    public void AddSimpleMediator_PreservesConfiguredStrategyAndCapacity_WhenMerged()
    {
        var services = new ServiceCollection();

        // First module configures custom Parallel strategy and custom capacity
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.OpenGenericResolutionCacheCapacity = 4096;
        });

        // Second module adds an assembly without touching strategy or capacity
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(UnitTest1).Assembly);
        });

        var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<MediatorConfiguration>();

        Assert.Equal(NotificationPublishStrategy.Parallel, config.NotificationPublishStrategy);
        Assert.Equal(4096, config.ResolutionCacheCapacity);
    }

    [Fact]
    public void AddSimpleMediator_AllowsExplicitOverrideOfStrategyAndCapacity_WhenMerged()
    {
        var services = new ServiceCollection();

        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
            options.OpenGenericResolutionCacheCapacity = 2048;
        });

        // Second registration explicitly sets Sequential and 512
        services.AddSimpleMediator(options =>
        {
            options.NotificationPublishStrategy = NotificationPublishStrategy.Sequential;
            options.OpenGenericResolutionCacheCapacity = 512;
        });

        var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<MediatorConfiguration>();

        Assert.Equal(NotificationPublishStrategy.Sequential, config.NotificationPublishStrategy);
        Assert.Equal(512, config.ResolutionCacheCapacity);
    }

    [Fact]
    public async Task BoundedFactoryCache_HandlesConcurrentAccessAndEviction()
    {
        var cache = new BoundedFactoryCache<int, string>(10);
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 100; j++)
            {
                var key = (i * 10 + j) % 30;
                var value = cache.GetOrAdd(key, k => $"val_{k}");
                Assert.Equal($"val_{key}", value);
            }
        }));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void BoundedFactoryCache_RetriesAfterFactoryFailure()
    {
        var cache = new BoundedFactoryCache<int, string>(1);
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd(1, _ =>
        {
            attempts++;
            throw new InvalidOperationException("transient failure");
        }));

        var value = cache.GetOrAdd(1, _ =>
        {
            attempts++;
            return "recovered";
        });

        Assert.Equal("recovered", value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void BoundedFactoryCache_DoesNotRetainFaultedKeysOrEvictTheirReplacement()
    {
        var cache = new BoundedFactoryCache<int, string>(2);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd(1, _ =>
                throw new InvalidOperationException("transient failure")));
        }

        Assert.Equal(0, cache.TrackedKeyCount);

        Assert.Equal("other", cache.GetOrAdd(2, _ => "other"));

        var successfulFactoryCalls = 0;
        Assert.Equal("recovered", cache.GetOrAdd(1, _ =>
        {
            successfulFactoryCalls++;
            return "recovered";
        }));
        Assert.Equal(1, successfulFactoryCalls);

        Assert.Equal("third", cache.GetOrAdd(3, _ => "third"));

        // A stale queue entry from the failed attempts must not evict the live entry.
        Assert.Equal("recovered", cache.GetOrAdd(1, _ =>
        {
            successfulFactoryCalls++;
            return "unexpected-recreation";
        }));
        Assert.Equal(1, successfulFactoryCalls);
        Assert.Equal(2, cache.TrackedKeyCount);
    }

    [Fact]
    public void Unit_IsReadonlyStruct()
    {
        Assert.True(typeof(Unit).IsValueType);
        // Struct should equal itself
        var u1 = Unit.Value;
        var u2 = new Unit();
        Assert.Equal(u1, u2);
        Assert.True(u1 == u2);
        Assert.False(u1 != u2);
    }

    /// <summary>
    /// Resolves a mediator the way an application must: from a scope, never from the root provider.
    /// A root-owned mediator would turn every scoped handler dependency into a process-wide
    /// instance, which <c>SimpleMediatorOptions.RequireScopedMediator</c> rejects by default. The
    /// scope is left to the provider, which disposes it along with everything else at test end.
    /// </summary>
    private static IMediator ResolveScopedMediator(IServiceProvider root)
        => root.CreateScope().ServiceProvider.GetRequiredService<IMediator>();

}
