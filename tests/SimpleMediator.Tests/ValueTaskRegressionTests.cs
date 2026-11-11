using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Tests;

public sealed class ValueTaskRegressionTests
{
    public interface IValueTaskTestRequest<T> : IRequest<T>;
    public sealed record Request(int Mode = 0) : IValueTaskTestRequest<int>;

    public sealed class ValueHandler : IValueTaskRequestHandler<Request, int>
    {
        public ValueTask<int> Handle(Request request, CancellationToken cancellationToken) => request.Mode switch
        {
            1 => throw new InvalidOperationException("synchronous"),
            2 => new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("faulted"))),
            3 => FailAsync(),
            4 => new ValueTask<int>(Task.FromCanceled<int>(new CancellationToken(true))),
            _ => new ValueTask<int>(42)
        };

        private static async ValueTask<int> FailAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("asynchronous");
        }
    }

    public sealed class TaskHandler : IRequestHandler<Request, int>
    {
        public Task<int> Handle(Request request, CancellationToken cancellationToken) => Task.FromResult(1);
    }

    public sealed class Recovery : IRequestExceptionHandler<Request, int>
    {
        public Task Handle(Request request, Exception exception, RequestExceptionHandlerState<int> state, CancellationToken cancellationToken)
        {
            state.SetHandled(99);
            return Task.CompletedTask;
        }
    }

    public sealed class ConstrainedTaskHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>, IDisposable
    {
        public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken) => Task.FromResult(default(TResponse)!);
    }

    public sealed class OpenValueHandler<TRequest, TResponse> : IValueTaskRequestHandler<TRequest, TResponse>
        where TRequest : IValueTaskTestRequest<TResponse>
    {
        public ValueTask<TResponse> Handle(TRequest request, CancellationToken cancellationToken) => new(default(TResponse)!);
    }

    public sealed record VtEchoRequest<T>(T Value) : IRequest<T>;

    public sealed class VtEchoHandler<T> : IValueTaskRequestHandler<VtEchoRequest<T>, T>, IDisposable
    {
        private readonly UnitTest1.CallProbe _probe;

        public VtEchoHandler(UnitTest1.CallProbe probe) => _probe = probe;

        public ValueTask<T> Handle(VtEchoRequest<T> request, CancellationToken cancellationToken)
            => new(request.Value);

        public void Dispose() => _probe.Record("vt-open-disposed");
    }

    [Fact]
    public async Task ValueTask_fast_path_routes_failures_and_preserves_cancellation()
    {
        var services = new ServiceCollection();
        services.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        services.AddTransient<IRequestExceptionHandler<Request, int>, Recovery>();
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(42, await sender.SendValueTask(new Request()));
        foreach (var mode in new[] { 1, 2, 3 })
        {
            Assert.Equal(99, await sender.SendValueTask(new Request(mode)));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sender.SendValueTask(new Request(4)));
    }

    [Fact]
    public async Task Mixed_handler_kinds_are_rejected_and_validation_catches_them()
    {
        var services = new ServiceCollection();
        services.AddTransient<IRequestHandler<Request, int>, TaskHandler>();
        services.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        Assert.Throws<RequestHandlerResolutionException>(() => services.ValidateSimpleMediator());
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<RequestHandlerResolutionException>(async () => await sender.SendValueTask(new Request()));
        await Assert.ThrowsAsync<RequestHandlerResolutionException>(async () => await sender.Send(new Request()));
    }

    [Fact]
    public void Validation_catches_value_task_duplicates_and_open_generic_conflicts()
    {
        var duplicates = new ServiceCollection();
        duplicates.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        duplicates.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        Assert.Throws<RequestHandlerResolutionException>(() => duplicates.ValidateSimpleMediator());

        var mixed = new ServiceCollection();
        mixed.AddTransient<IRequestHandler<Request, int>, TaskHandler>();
        mixed.AddTransient(typeof(IValueTaskRequestHandler<,>), typeof(OpenValueHandler<,>));
        Assert.Throws<RequestHandlerResolutionException>(() => mixed.ValidateSimpleMediator());
    }

    [Fact]
    public async Task Constrained_task_handler_does_not_hide_value_handler_on_subsequent_sends()
    {
        var services = new ServiceCollection();
        services.AddTransient(typeof(IRequestHandler<,>), typeof(ConstrainedTaskHandler<,>));
        services.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        services.AddSimpleMediator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(42, await sender.Send(new Request()));
        Assert.Equal(42, await sender.Send(new Request()));
        Assert.Equal(42, await sender.SendValueTask(new Request()));
    }

    [Fact]
    public async Task Assembly_scan_registers_native_open_value_handler()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(Request).Assembly, type => type == typeof(OpenValueHandler<,>)));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(0, await sender.SendValueTask(new Request()));
        Assert.Equal(0, await sender.SendValueTask(new Request()));
    }

    [Fact]
    public async Task Assembly_scan_uses_custom_inference_for_value_task_open_generic_handler()
    {
        var probe = new UnitTest1.CallProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddSimpleMediator(options =>
        {
            options.DefaultLifetime = ServiceLifetime.Transient;
            options.RegisterAssembly(typeof(VtEchoRequest<>).Assembly, type => type == typeof(VtEchoHandler<>));
        });

        var configuration = Assert.IsType<MediatorConfiguration>(services
            .Single(service => service.ServiceType == typeof(MediatorConfiguration))
            .ImplementationInstance);
        Assert.Contains(configuration.CustomOpenGenericRequestHandlers,
            registration => registration.ImplementationType == typeof(VtEchoHandler<>));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IValueTaskRequestHandler<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(7, await sender.Send(new VtEchoRequest<int>(7)));
        Assert.Equal("x", await sender.Send(new VtEchoRequest<string>("x")));
        Assert.Contains("vt-open-disposed", probe.Events);
    }

    [Fact]
    public void Assembly_scan_does_not_register_simplemediator_own_infrastructure_handlers()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options => options.RegisterAssembly(typeof(IMediator).Assembly));

        var configuration = Assert.IsType<MediatorConfiguration>(services
            .Single(service => service.ServiceType == typeof(MediatorConfiguration))
            .ImplementationInstance);
        Assert.Empty(configuration.CustomOpenGenericRequestHandlers);
        Assert.DoesNotContain(services, service =>
            service.ImplementationType is { IsGenericTypeDefinition: true } &&
            (service.ServiceType == typeof(IRequestHandler<,>) ||
             service.ServiceType == typeof(IValueTaskRequestHandler<,>)));
    }

    [Fact]
    public async Task ValueTask_fast_path_emits_telemetry_for_each_request()
    {
        var started = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SimpleMediatorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == nameof(Request)) Interlocked.Increment(ref started);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var services = new ServiceCollection();
        services.AddTransient<IValueTaskRequestHandler<Request, int>, ValueHandler>();
        services.AddSimpleMediator(options => options.EnableTelemetry = true);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.SendValueTask(new Request());
        await sender.SendValueTask(new Request());
        Assert.Equal(2, started);
    }
}
