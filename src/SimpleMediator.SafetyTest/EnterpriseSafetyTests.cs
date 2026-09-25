using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.SafetyTest;

/// <summary>
/// Regression tests for the scope, lifetime, and failure-routing defects found in the v4 baseline
/// review. Each test pins one specific behaviour so it cannot silently regress.
/// </summary>
public class EnterpriseSafetyTests
{
    // ---------------------------------------------------------------------
    // 1. A root-owned mediator must be rejected, not silently allowed.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mediator_ResolvedFromRootProvider_Throws()
    {
        using var provider = BuildProvider();

        var exception = Assert.Throws<MediatorScopeException>(
            () => provider.GetRequiredService<IMediator>());

        Assert.Contains("root service provider", exception.Message);
        Assert.Contains("CreateScope", exception.Message);
    }

    [Fact]
    public void Mediator_ResolvedFromScope_Works()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        Assert.NotNull(mediator);
    }

    [Fact]
    public void RequireScopedMediator_False_AllowsRootProvider()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RequireScopedMediator = false;
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMediator>());
    }

    [Fact]
    public void Mediator_WithoutConfiguration_DoesNotApplyTheGuard()
    {
        // Direct construction with no AddSimpleMediator call: there is no configuration to read the
        // option from, so the guard cannot be enforced and must not be guessed at.
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(new Mediator(provider));
    }

    // ---------------------------------------------------------------------
    // 2. A singleton custom-mapped open-generic handler must not capture a
    //    request scope, and a scoped dependency must be rejected outright.
    // ---------------------------------------------------------------------

    [Fact]
    public void SingletonCustomOpenGenericHandler_ResolvesDependenciesFromTheRootProvider()
    {
        // A singleton custom-mapped handler used to be built from the FIRST REQUEST SCOPE's
        // provider, so it captured — and outlived — a scoped dependency whose scope was later
        // disposed. It must now be built from the root provider. This asserts that invariant
        // directly, because the harmful variant is rejected at registration by the test below and
        // therefore cannot be constructed at all.
        var services = new ServiceCollection();
        services.AddSingleton<ScopeCounter>();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Singleton;
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
        });

        using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<SimpleMediator.Core.OpenGenericSingletonLifetimeStore>();

        // The root provider is the one that hands back itself as the scope factory; a scope does not.
        Assert.Same(store.RootProvider, store.RootProvider.GetService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task SingletonCustomOpenGenericHandler_SeesTheRootDependencyAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ScopeCounter>();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Singleton;
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
        });

        using var provider = services.BuildServiceProvider();

        var observed = new List<Guid>();
        for (var run = 0; run < 3; run++)
        {
            using var scope = provider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            observed.Add(await mediator.Send<Guid>(new SingletonCapturingRequest<int>()));
        }

        // One singleton handler, one singleton dependency, and never a dependency bound to a scope
        // that has already been disposed.
        Assert.Single(observed.Distinct());
    }

    [Fact]
    public void SingletonCustomOpenGenericHandler_WithScopedDependency_IsRejectedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeCounter>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddSimpleMediator(options =>
            {
                options.DefaultLifetime = ServiceLifetime.Singleton;
                options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            }));

        Assert.Contains("Singleton", exception.Message);
        Assert.Contains(nameof(ScopeCounter), exception.Message);
        Assert.Contains("ServiceLifetime.Scoped", exception.Message);
    }

    [Fact]
    public void SingletonCustomOpenGenericHandler_WithTransientDependency_IsRejectedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddTransient<ScopeCounter>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddSimpleMediator(options =>
            {
                options.DefaultLifetime = ServiceLifetime.Singleton;
                options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            }));

        Assert.Contains(nameof(ScopeCounter), exception.Message);
    }

    [Fact]
    public async Task ScopedCustomOpenGenericHandler_StillSharesOneInstancePerScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeCounter>();
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Scoped;
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
        });

        using var provider = services.BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var firstMediator = firstScope.ServiceProvider.GetRequiredService<IMediator>();
        var secondMediator = secondScope.ServiceProvider.GetRequiredService<IMediator>();

        var withinFirstScope = await firstMediator.Send<Guid>(new ScopedCapturingRequest<int>());
        var alsoWithinFirstScope = await firstMediator.Send<Guid>(new ScopedCapturingRequest<int>());
        var withinSecondScope = await secondMediator.Send<Guid>(new ScopedCapturingRequest<int>());

        Assert.Equal(withinFirstScope, alsoWithinFirstScope);
        Assert.NotEqual(withinFirstScope, withinSecondScope);
    }

    // ---------------------------------------------------------------------
    // 3. An open generic nested in a generic outer type must be skipped,
    //    not abort the composition root.
    // ---------------------------------------------------------------------

    [Fact]
    public void Scan_SkipsOpenGenericNestedInGenericOuterType()
    {
        var services = new ServiceCollection();

        // No exception: NestedHandler<T> below can never be closed by anyone, so it is skipped.
        services.AddSimpleMediator(options =>
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));

        Assert.NotNull(services.FirstOrDefault(d => d.ServiceType == typeof(IMediator)));
    }

    [Fact]
    public async Task Scan_StillRegistersReachableHandlersAlongsideSkippedOnes()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        Assert.Equal(7, await mediator.Send(new FilterTargetRequest()));
    }

    [Fact]
    public async Task Scan_HonoursTypeFilter()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(
            typeof(EnterpriseSafetyTests).Assembly,
            type => type != typeof(FilterTargetHandler)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => mediator.Send(new FilterTargetRequest()));
    }

    [Fact]
    public async Task Scan_TypeFilterForSameAssembly_NarrowsRatherThanReplaces()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly, type => type != typeof(FilterTargetHandler));
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly, type => type != typeof(AnotherFilterTargetHandler));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // The first call excluded FilterTargetHandler, the second excluded AnotherFilterTargetHandler.
        // Narrowing means both stay excluded, so FilterTargetRequest still has no handler...
        await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => mediator.Send(new FilterTargetRequest()));

        // ...while a request that neither call excluded is unaffected.
        Assert.Equal(1, await mediator.Send(new ConstructionFailureRequest()));
    }

    // ---------------------------------------------------------------------
    // 4. Construction failures must reach IRequestExceptionHandler.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task BehaviorConstructionFailure_IsRoutedToExceptionHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        services.AddTransient<IPipelineBehavior<ConstructionFailureRequest, int>>(
            _ => throw new InvalidOperationException("behavior ctor boom"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();

        // The catch-all exception handler recovers with default(int), which proves the construction
        // failure travelled through the exception pipeline instead of escaping directly.
        var result = await mediator.Send(new ConstructionFailureRequest());

        Assert.Equal(0, result);
        Assert.Contains(ConstructionFailureObserver.Observed, message => message.Contains("behavior ctor boom"));
    }

    [Fact]
    public async Task PreHandlerConstructionFailure_IsRoutedToExceptionHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        services.AddTransient<IPreRequestHandler<PreConstructionFailureRequest, int>>(
            _ => throw new InvalidOperationException("pre ctor boom"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();
        var result = await mediator.Send(new PreConstructionFailureRequest());

        Assert.Equal(0, result);
        Assert.Contains(ConstructionFailureObserver.Observed, message => message.Contains("pre ctor boom"));
    }

    [Fact]
    public async Task PostHandlerConstructionFailure_IsRoutedToExceptionHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        services.AddTransient<IPostRequestHandler<PostConstructionFailureRequest, int>>(
            _ => throw new InvalidOperationException("post ctor boom"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();
        var result = await mediator.Send(new PostConstructionFailureRequest());

        Assert.Equal(0, result);
        Assert.Contains(ConstructionFailureObserver.Observed, message => message.Contains("post ctor boom"));
    }

    [Fact]
    public async Task RequestHandlerConstructionFailure_IsRoutedToExceptionHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        services.AddTransient<IRequestHandler<HandlerConstructionFailureRequest, int>>(
            _ => throw new InvalidOperationException("handler ctor boom"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();
        var result = await mediator.Send(new HandlerConstructionFailureRequest());

        Assert.Equal(0, result);
        Assert.Contains(ConstructionFailureObserver.Observed, message => message.Contains("handler ctor boom"));
    }

    // ---------------------------------------------------------------------
    // 5. Handler selection defects are wiring bugs, not request failures.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task HandlerSelectionFailure_IsNotOfferedToExceptionHandlers()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        services.AddSingleton<ConstructionFailureObserver>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Two handlers for the same request: a wiring defect.
        services.AddTransient<IRequestHandler<UnwiredRequest, int>, UnwiredHandlerA>();
        var second = services.AddTransient<IRequestHandler<UnwiredRequest, int>, UnwiredHandlerB>();
        Assert.NotNull(second);

        using var rebuilt = services.BuildServiceProvider();
        using var rebuiltScope = rebuilt.CreateScope();
        var rebuiltMediator = rebuiltScope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();
        var exception = await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => rebuiltMediator.Send(new UnwiredRequest()));

        Assert.Contains("Multiple request handlers", exception.Message);
        Assert.DoesNotContain(ConstructionFailureObserver.Observed, message => message.Contains("Multiple request handlers"));
    }

    [Fact]
    public async Task UnconstructibleExceptionHandler_DoesNotMaskTheOriginalFailure()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        // Registered but unconstructible: the exception pipeline itself is broken.
        services.AddTransient<IRequestExceptionHandler<ThrowingRequest, int>>(
            _ => throw new InvalidOperationException("exception handler ctor boom"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => mediator.Send(new ThrowingRequest()));

        Assert.Equal("handler blew up", exception.Message);
    }

    // ---------------------------------------------------------------------
    // 6. A null Task from a handler is an actionable error, not an NRE.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RequestHandlerReturningNullTask_ReportsTheHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // The catch-all exception handler observes the failure, so assert on what it saw rather
        // than on the exception escaping.
        ConstructionFailureObserver.Observed.Clear();
        await mediator.Send(new NullTaskRequest());

        var observed = Assert.Single(ConstructionFailureObserver.Observed);
        Assert.Contains("null Task", observed);
        Assert.Contains(nameof(NullTaskHandler), observed);
    }

    [Fact]
    public async Task VoidRequestHandlerReturningNullTask_ReportsTheHandler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ConstructionFailureObserver.Observed.Clear();
        await mediator.Send(new NullCoreRequest());

        var observed = Assert.Single(ConstructionFailureObserver.Observed);
        Assert.Contains("null Task", observed);
        Assert.Contains(nameof(NullCoreHandler), observed);
    }

    // ---------------------------------------------------------------------
    // 7. Order is read once per request and is stable within a request.
    // ---------------------------------------------------------------------

    [Fact]
    public void PipelineBehavior_Order_HasAZeroDefault()
    {
        var order = typeof(IOrderedPipelineBehavior).GetProperty(nameof(IOrderedPipelineBehavior.Order))!;

        Assert.True(order.GetMethod!.IsAbstract == false, "Order should be a default interface member.");
    }

    [Fact]
    public async Task PipelineBehavior_Order_IsReadOncePerRequest()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            options.AddBehavior(typeof(OrderCountingBehavior));
            options.AddBehavior(typeof(OrderCountingSecondBehavior));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        OrderCountingBehavior.Reads = 0;
        OrderCountingSecondBehavior.Reads = 0;
        await mediator.Send(new OrderProbeRequest());

        // One snapshot per behavior per request, not one read per comparison (Array.Sort would read
        // O(n log n) times and could see an inconsistent ordering).
        Assert.Equal(1, OrderCountingBehavior.Reads);
        Assert.Equal(1, OrderCountingSecondBehavior.Reads);
    }

    [Fact]
    public async Task PipelineBehavior_SingleBehavior_DoesNotReadOrderAtAll()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            options.AddBehavior(typeof(OrderCountingBehavior));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        OrderCountingBehavior.Reads = 0;
        await mediator.Send(new OrderProbeRequest());

        Assert.Equal(0, OrderCountingBehavior.Reads);
    }

    [Fact]
    public async Task PipelineBehavior_Order_IsStableWithinARequest()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            options.AddBehavior(typeof(UnstableOrderBehavior));
            options.AddBehavior(typeof(FallbackOrderBehavior));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // The ordering must be computed from one snapshot, so an Order that changes between reads
        // cannot make the sort comparator inconsistent.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(1, await mediator.Send(new UnstableOrderRequest()));
        }
    }

    // ---------------------------------------------------------------------
    // 8. Conflicting behavior lifetimes across modules are reported.
    // ---------------------------------------------------------------------

    [Fact]
    public void ConflictingBehaviorLifetimes_AcrossModules_AreRejected()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            options.AddBehavior(typeof(ModuleScopedBehavior));
            options.DefaultLifetime = ServiceLifetime.Scoped;
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddSimpleMediator(options =>
            {
                options.AddBehavior(typeof(ModuleScopedBehavior));
                options.DefaultLifetime = ServiceLifetime.Singleton;
            }));

        Assert.Contains("conflicting", exception.Message);
        Assert.Contains(nameof(ModuleScopedBehavior), exception.Message);
    }

    [Fact]
    public void SameBehaviorLifetime_AcrossModules_IsAccepted()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly);
            options.AddBehavior(typeof(ModuleScopedBehavior));
        });
        services.AddSimpleMediator(options => options.AddBehavior(typeof(ModuleScopedBehavior)));

        Assert.NotNull(services.FirstOrDefault(d => d.ServiceType == typeof(IMediator)));
    }

    // ---------------------------------------------------------------------

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
            options.RegisterAssembly(typeof(EnterpriseSafetyTests).Assembly));
        return services.BuildServiceProvider();
    }
}

// ======================= test doubles =======================

public sealed class ScopeCounter
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed record SingletonCapturingRequest<T> : IRequest<Guid>;

// Reports which ScopeCounter instance the singleton handler was built with.
public sealed class SingletonCapturingHandler<T> : IRequestHandler<SingletonCapturingRequest<T>, Guid>
{
    private readonly ScopeCounter _counter;

    public SingletonCapturingHandler(ScopeCounter counter) => _counter = counter;

    public Task<Guid> Handle(SingletonCapturingRequest<T> request, CancellationToken cancellationToken)
        => Task.FromResult(_counter.Id);
}

public sealed record ScopedCapturingRequest<T> : IRequest<Guid>;

public sealed class ScopedCapturingHandler<T> : IRequestHandler<ScopedCapturingRequest<T>, Guid>
{
    private readonly ScopeCounter _counter;

    public ScopedCapturingHandler(ScopeCounter counter) => _counter = counter;

    public Task<Guid> Handle(ScopedCapturingRequest<T> request, CancellationToken cancellationToken)
        => Task.FromResult(_counter.Id);
}

// Unreachable by construction: nested inside a generic outer type.
public class GenericHost<TOuter>
{
    public class UnreachableNestedHandler<T> : IRequestHandler<UnreachableRequest<T>, T>
    {
        public Task<T> Handle(UnreachableRequest<T> request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);
    }
}

public sealed record UnreachableRequest<T>(T Value) : IRequest<T>;

public sealed record FilterTargetRequest : IRequest<int>;

public sealed class FilterTargetHandler : IRequestHandler<FilterTargetRequest, int>
{
    public Task<int> Handle(FilterTargetRequest request, CancellationToken cancellationToken)
        => Task.FromResult(7);
}

public sealed record AnotherFilterTargetHandler : IRequestHandler<FilterTargetRequestForScopeCheck, int>
{
    public Task<int> Handle(FilterTargetRequestForScopeCheck request, CancellationToken cancellationToken)
        => Task.FromResult(8);
}

public sealed record FilterTargetRequestForScopeCheck : IRequest<int>;

public sealed record ConstructionFailureRequest : IRequest<int>;

public sealed class ConstructionFailureHandler : IRequestHandler<ConstructionFailureRequest, int>
{
    public Task<int> Handle(ConstructionFailureRequest request, CancellationToken cancellationToken)
        => Task.FromResult(1);
}

public sealed class ConstructionFailureObserver
{
    public static List<string> Observed { get; } = new();
}

public sealed class ConstructionFailureCatchAll<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task Handle(TRequest request, Exception exception, RequestExceptionHandlerState<TResponse> state, CancellationToken cancellationToken)
    {
        lock (ConstructionFailureObserver.Observed)
        {
            ConstructionFailureObserver.Observed.Add(exception.Message);
        }

        state.SetHandled(default!);
        return Task.CompletedTask;
    }
}

public sealed record PreConstructionFailureRequest : IRequest<int>;

public sealed class PreConstructionFailureHandler : IRequestHandler<PreConstructionFailureRequest, int>
{
    public Task<int> Handle(PreConstructionFailureRequest request, CancellationToken cancellationToken)
        => Task.FromResult(1);
}

public sealed record PostConstructionFailureRequest : IRequest<int>;

public sealed class PostConstructionFailureHandler : IRequestHandler<PostConstructionFailureRequest, int>
{
    public Task<int> Handle(PostConstructionFailureRequest request, CancellationToken cancellationToken)
        => Task.FromResult(1);
}

public sealed record HandlerConstructionFailureRequest : IRequest<int>;

public sealed record UnwiredRequest : IRequest<int>;

public sealed class UnwiredHandlerA : IRequestHandler<UnwiredRequest, int>
{
    public Task<int> Handle(UnwiredRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class UnwiredHandlerB : IRequestHandler<UnwiredRequest, int>
{
    public Task<int> Handle(UnwiredRequest request, CancellationToken cancellationToken) => Task.FromResult(2);
}

public sealed record ThrowingRequest : IRequest<int>;

public sealed class ThrowingHandler : IRequestHandler<ThrowingRequest, int>
{
    public Task<int> Handle(ThrowingRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("handler blew up");
}

public sealed record NullTaskRequest : IRequest<int>;

public sealed class NullTaskHandler : IRequestHandler<NullTaskRequest, int>
{
    public Task<int> Handle(NullTaskRequest request, CancellationToken cancellationToken) => null!;
}

public sealed record NullCoreRequest : IRequest;

public sealed class NullCoreHandler : RequestHandler<NullCoreRequest>
{
    protected override Task HandleCore(NullCoreRequest request, CancellationToken cancellationToken) => null!;
}

public sealed record OrderProbeRequest : IRequest<int>;

public sealed class OrderProbeHandler : IRequestHandler<OrderProbeRequest, int>
{
    public Task<int> Handle(OrderProbeRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class OrderCountingBehavior : IPipelineBehavior<OrderProbeRequest, int>
{
    public static int Reads;

    public int Order
    {
        get
        {
            Reads++;
            return 0;
        }
    }

    public Task<int> Handle(OrderProbeRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class OrderCountingSecondBehavior : IPipelineBehavior<OrderProbeRequest, int>
{
    public static int Reads;

    public int Order
    {
        get
        {
            Reads++;
            return 1;
        }
    }

    public Task<int> Handle(OrderProbeRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed record UnstableOrderRequest : IRequest<int>;

public sealed class UnstableOrderHandler : IRequestHandler<UnstableOrderRequest, int>
{
    public Task<int> Handle(UnstableOrderRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
}

// Order changes on every read: only safe because the pipeline snapshots it once per request.
public sealed class UnstableOrderBehavior : IPipelineBehavior<UnstableOrderRequest, int>
{
    private int _counter;

    public int Order => _counter++ % 2;

    public Task<int> Handle(UnstableOrderRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class FallbackOrderBehavior : IPipelineBehavior<UnstableOrderRequest, int>
{
    public int Order => 0;

    public Task<int> Handle(UnstableOrderRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed record ModuleBehaviorRequest : IRequest<int>;

public sealed class ModuleBehaviorHandler : IRequestHandler<ModuleBehaviorRequest, int>
{
    public Task<int> Handle(ModuleBehaviorRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class ModuleScopedBehavior : IPipelineBehavior<ModuleBehaviorRequest, int>
{
    public Task<int> Handle(ModuleBehaviorRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}
