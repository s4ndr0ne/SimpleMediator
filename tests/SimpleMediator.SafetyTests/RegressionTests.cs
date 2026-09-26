using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Configuration;
using SimpleMediator.Core;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.SafetyTests;

/// <summary>
/// Regression tests for the defects found in the post-v4 review. Each test pins one fix.
/// </summary>
public class RegressionTests
{
    // ---------------------------------------------------------------------
    // Wrapper caches must not evict: more than 1024 distinct request types
    // used to thrash a bounded FIFO cache and rebuild wrappers on every call.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WrapperCache_RetainsEveryDispatchedRequestType_BeyondTheOldCapacity()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IRequestHandler<,>), typeof(RegressionPassThroughHandler<,>));
        services.AddSimpleMediator();

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var configuration = provider.GetRequiredService<MediatorConfiguration>();

        var requests = DistinctRequests(1100);
        foreach (var request in requests)
        {
            Assert.Equal(7, await sender.Send(request));
        }

        Assert.Equal(1100, configuration.RequestHandlerWrappers.Count);

        // A second pass reuses every wrapper instead of recreating evicted ones.
        foreach (var request in requests)
        {
            await sender.Send(request);
        }

        Assert.Equal(1100, configuration.RequestHandlerWrappers.Count);
    }

    [Fact]
    public async Task WrapperCache_IsSingleFlight_AndRetriesAfterAFault()
    {
        var cache = new WrapperCache<int>();
        var calls = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            cache.GetOrAdd(1, _ =>
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(20);
                return new object();
            }))));

        Assert.Equal(1, calls);
        Assert.Single(results.Distinct());

        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd(2, _ => throw new InvalidOperationException("transient")));
        Assert.Equal("recovered", cache.GetOrAdd(2, _ => "recovered"));
    }

    // ---------------------------------------------------------------------
    // A null Task must be attributed to the behavior that returned it.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NullTask_FromOutermostBehavior_NamesThatBehavior()
    {
        var exception = await SendWithBehaviors(typeof(NullReturningOuterBehavior), typeof(PassThroughInnerBehavior));

        Assert.Contains(nameof(NullReturningOuterBehavior), exception.Message);
        Assert.DoesNotContain(nameof(PassThroughInnerBehavior), exception.Message);
    }

    [Fact]
    public async Task NullTask_FromInnermostBehavior_NamesThatBehavior()
    {
        var exception = await SendWithBehaviors(typeof(PassThroughOuterBehavior), typeof(NullReturningInnerBehavior));

        Assert.Contains(nameof(NullReturningInnerBehavior), exception.Message);
        Assert.DoesNotContain(nameof(PassThroughOuterBehavior), exception.Message);
    }

    [Fact]
    public async Task NullTask_FromMiddleBehavior_NamesThatBehavior()
    {
        var exception = await SendWithBehaviors(
            typeof(PassThroughOuterBehavior),
            typeof(NullReturningMiddleBehavior),
            typeof(PassThroughInnerBehavior));

        Assert.Contains(nameof(NullReturningMiddleBehavior), exception.Message);
    }

    // ---------------------------------------------------------------------
    // The root guard must also catch a mediator constructed by hand.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mediator_ConstructedWithTheRootProvider_Throws()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<MediatorScopeException>(() => new Mediator(provider));
    }

    [Fact]
    public void Mediator_ConstructedWithAScopeProvider_Works()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var nested = scope.ServiceProvider.CreateScope();

        Assert.NotNull(new Mediator(scope.ServiceProvider));
        Assert.NotNull(new Mediator(nested.ServiceProvider));
    }

    [Fact]
    public void Mediator_ConstructedWithTheRootProvider_IsAllowedWhenTheGuardIsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RequireScopedMediator = false);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(new Mediator(provider));
    }

    // ---------------------------------------------------------------------
    // RequireScopedMediator across modules: explicit beats default, and
    // conflicting explicit values are an error instead of a silent AND.
    // ---------------------------------------------------------------------

    [Fact]
    public void RequireScopedMediator_ExplicitFalse_ThenDefaultModule_StaysDisabled()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RequireScopedMediator = false);
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMediator>());
    }

    [Fact]
    public void RequireScopedMediator_DefaultModule_ThenExplicitFalse_IsDisabled()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();
        services.AddSimpleMediator(options => options.RequireScopedMediator = false);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMediator>());
    }

    [Fact]
    public void RequireScopedMediator_ExplicitTrue_ThenDefaultModule_StaysEnabled()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RequireScopedMediator = true);
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<MediatorScopeException>(() => provider.GetRequiredService<IMediator>());
    }

    [Fact]
    public void RequireScopedMediator_ConflictingExplicitValues_Throw()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RequireScopedMediator = true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddSimpleMediator(options => options.RequireScopedMediator = false));

        Assert.Contains("conflicting", exception.Message);
    }

    [Fact]
    public void RequireScopedMediator_SameExplicitValueInEveryModule_IsAccepted()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RequireScopedMediator = false);
        services.AddSimpleMediator(options => options.RequireScopedMediator = false);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMediator>());
    }

    // ---------------------------------------------------------------------
    // Singleton custom-mapped handler validation. The handlers are emitted
    // into dynamic assemblies so that assembly scanning in other tests never
    // discovers them.
    // ---------------------------------------------------------------------

    [Fact]
    public void SingletonHandler_UsesTheLastRegistration_ScopedOverrideIsRejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RegressionDependency>();
        services.AddScoped<RegressionDependency>(); // DI resolves this one

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddSingletonEmittedHandler(services, _ => new[] { typeof(RegressionDependency) }));

        Assert.Contains("Scoped", exception.Message);
    }

    [Fact]
    public void SingletonHandler_UsesTheLastRegistration_SingletonOverrideIsAccepted()
    {
        var services = new ServiceCollection();
        services.AddScoped<RegressionDependency>();
        services.AddSingleton<RegressionDependency>(); // DI resolves this one

        AddSingletonEmittedHandler(services, _ => new[] { typeof(RegressionDependency) });
    }

    [Fact]
    public void SingletonHandler_WithOpenGenericSingletonDependency_IsAccepted()
    {
        // The ILogger<THandler> shape: a dependency closed over the handler's own type parameter,
        // registered as an open-generic singleton. It used to be rejected as "unknowable".
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IRegressionOpenDependency<>), typeof(RegressionOpenDependency<>));

        AddSingletonEmittedHandler(services, t => new[] { typeof(IRegressionOpenDependency<>).MakeGenericType(t) });
    }

    [Fact]
    public void SingletonHandler_WithOpenGenericScopedDependency_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddScoped(typeof(IRegressionOpenDependency<>), typeof(RegressionOpenDependency<>));

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddSingletonEmittedHandler(services, t => new[] { typeof(IRegressionOpenDependency<>).MakeGenericType(t) }));

        Assert.Contains("Scoped", exception.Message);
    }

    [Fact]
    public void SingletonHandler_WithUnregisteredOpenGenericDependency_IsRejectedAsUnknowable()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddSingletonEmittedHandler(services, t => new[] { typeof(IRegressionOpenDependency<>).MakeGenericType(t) }));

        Assert.Contains("open generic", exception.Message);
    }

    [Fact]
    public void SingletonHandler_WithEnumerableContainingAScopedRegistration_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RegressionDependency>();
        services.AddScoped<RegressionDependency>();
        services.AddSingleton<RegressionDependency>(); // last is singleton, but IEnumerable gets all three

        var exception = Assert.Throws<InvalidOperationException>(
            () => AddSingletonEmittedHandler(services, _ => new[] { typeof(IEnumerable<RegressionDependency>) }));

        Assert.Contains("Scoped", exception.Message);
    }

    [Fact]
    public void SingletonHandler_IgnoresKeyedRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RegressionDependency>();
        services.AddKeyedScoped<RegressionDependency>("tenant");

        AddSingletonEmittedHandler(services, _ => new[] { typeof(RegressionDependency) });
    }

    // ---------------------------------------------------------------------
    // Helpers and test types
    // ---------------------------------------------------------------------

    private static async Task<InvalidOperationException> SendWithBehaviors(params Type[] behaviors)
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<RegressionPingRequest, int>, RegressionPingHandler>();
        services.AddSimpleMediator(options =>
        {
            foreach (var behavior in behaviors)
            {
                options.AddBehavior(behavior);
            }
        });

        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        return await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new RegressionPingRequest()));
    }

    private static List<IRequest<int>> DistinctRequests(int count)
    {
        var basis = new[]
        {
            typeof(int), typeof(long), typeof(short), typeof(byte), typeof(string), typeof(double), typeof(float),
            typeof(decimal), typeof(char), typeof(bool), typeof(Guid), typeof(DateTime), typeof(TimeSpan),
            typeof(uint), typeof(ulong), typeof(ushort), typeof(sbyte), typeof(object), typeof(Uri), typeof(Version),
            typeof(DateTimeOffset), typeof(Exception), typeof(Type), typeof(Array), typeof(Delegate),
            typeof(Attribute), typeof(Random), typeof(Half), typeof(Int128), typeof(UInt128), typeof(nint),
            typeof(nuint), typeof(DateOnly), typeof(TimeOnly),
        };

        var requests = new List<IRequest<int>>(count);
        foreach (var first in basis)
        {
            foreach (var second in basis)
            {
                if (requests.Count == count)
                {
                    return requests;
                }

                var requestType = typeof(RegressionGenericRequest<>).MakeGenericType(typeof(Tuple<,>).MakeGenericType(first, second));
                requests.Add((IRequest<int>)Activator.CreateInstance(requestType)!);
            }
        }

        throw new InvalidOperationException("Not enough distinct request types.");
    }

    private static void AddSingletonEmittedHandler(IServiceCollection services, Func<Type, Type[]> constructorParameters)
    {
        var assembly = EmitCustomOpenGenericHandler(constructorParameters);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Singleton;
            options.RegisterAssembly(assembly);
        });
    }

    private static int s_emittedAssemblies;

    /// <summary>
    /// Emits <c>EmittedHandler&lt;T&gt; : IRequestHandler&lt;EmittedRequest&lt;T&gt;, int&gt;</c> whose single
    /// public constructor takes the parameter types produced by <paramref name="constructorParameters"/>
    /// (given the handler's generic parameter <c>T</c>). Validation only inspects the shape, so the
    /// methods simply throw.
    /// </summary>
    private static Assembly EmitCustomOpenGenericHandler(Func<Type, Type[]> constructorParameters)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"RegressionEmittedHandlers{Interlocked.Increment(ref s_emittedAssemblies)}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Handlers");
        var typeBuilder = module.DefineType("EmittedHandler`1", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        var typeParameter = typeBuilder.DefineGenericParameters("T")[0];

        var requestType = typeof(RegressionEmittedRequest<>).MakeGenericType(typeParameter);
        typeBuilder.AddInterfaceImplementation(typeof(IRequestHandler<,>).MakeGenericType(requestType, typeof(int)));

        var constructor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, constructorParameters(typeParameter));
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Ret);

        var handle = typeBuilder.DefineMethod(
            "Handle",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.HideBySig,
            typeof(Task<int>),
            new[] { requestType, typeof(CancellationToken) });
        var handleIl = handle.GetILGenerator();
        handleIl.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor(Type.EmptyTypes)!);
        handleIl.Emit(OpCodes.Throw);

        _ = typeBuilder.CreateType();
        return assembly;
    }
}

/// <summary>Marks the only requests <see cref="RegressionPassThroughHandler{TRequest, TResponse}"/> may close over.</summary>
public interface IRegressionCacheRequest
{
}

public sealed record RegressionGenericRequest<T> : IRequest<int>, IRegressionCacheRequest;

public sealed record RegressionEmittedRequest<T> : IRequest<int>;

// The IRegressionCacheRequest constraint keeps this native open-generic handler from matching the
// requests of other tests that scan this assembly: Microsoft DI skips open generics whose
// constraints the requested type does not satisfy.
public sealed class RegressionPassThroughHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IRegressionCacheRequest
{
    public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken)
        => Task.FromResult((TResponse)(object)7);
}

public sealed class RegressionDependency
{
}

public interface IRegressionOpenDependency<T>
{
}

public sealed class RegressionOpenDependency<T> : IRegressionOpenDependency<T>
{
}

public sealed record RegressionPingRequest : IRequest<int>;

public sealed class RegressionPingHandler : IRequestHandler<RegressionPingRequest, int>
{
    public Task<int> Handle(RegressionPingRequest request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class PassThroughOuterBehavior : IPipelineBehavior<RegressionPingRequest, int>
{
    public int Order => 0;

    public Task<int> Handle(RegressionPingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class NullReturningOuterBehavior : IPipelineBehavior<RegressionPingRequest, int>
{
    public int Order => 0;

    public Task<int> Handle(RegressionPingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => null!;
}

public sealed class NullReturningMiddleBehavior : IPipelineBehavior<RegressionPingRequest, int>
{
    public int Order => 1;

    public Task<int> Handle(RegressionPingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => null!;
}

public sealed class PassThroughInnerBehavior : IPipelineBehavior<RegressionPingRequest, int>
{
    public int Order => 2;

    public Task<int> Handle(RegressionPingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

public sealed class NullReturningInnerBehavior : IPipelineBehavior<RegressionPingRequest, int>
{
    public int Order => 2;

    public Task<int> Handle(RegressionPingRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken)
        => null!;
}
