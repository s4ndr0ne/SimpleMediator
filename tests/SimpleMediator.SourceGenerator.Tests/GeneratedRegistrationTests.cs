using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Generated;
using SimpleMediator.Interfaces;
using SimpleMediator.SourceGenerator.Tests.Fixtures;

namespace SimpleMediator.SourceGenerator.Tests;

/// <summary>
/// End-to-end tests of the code generated for this project: AddSimpleMediatorGenerated must behave
/// exactly like the reflection-based AddSimpleMediator it replaces.
/// </summary>
public sealed class GeneratedRegistrationTests
{
    private static void Configure(SimpleMediatorOptions options)
    {
        options.RegisterAssembly(typeof(Ping).Assembly, type => type != typeof(HiddenHandler));
        options.AddBehavior(typeof(OuterBehavior<,>));
        options.AddBehavior(typeof(InnerBehavior<,>));
        options.AddBehavior(typeof(CommandOnlyBehavior<,>));
        options.AddBehavior(typeof(PingOnlyBehavior));
    }

    private static ServiceProvider Build(bool generated, Action<SimpleMediatorOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Trace>();

        void ConfigureAll(SimpleMediatorOptions options)
        {
            Configure(options);
            extra?.Invoke(options);
        }

        if (generated)
        {
            services.AddSimpleMediatorGenerated(ConfigureAll);
        }
        else
        {
            services.AddSimpleMediator(ConfigureAll);
        }

        return services.BuildServiceProvider();
    }

    private static async Task<List<string>> RunScenario(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var results = new List<string>
        {
            await mediator.Send(new Ping("a")),
            (await mediator.Send(new Count(1))).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await mediator.Send(new Fire("x"));
        results.Add((await mediator.Send(new Echo<int>(5))).ToString(System.Globalization.CultureInfo.InvariantCulture));
        results.Add(await mediator.Send(new Echo<string>("s")));
        results.Add(await mediator.Send(new Explode("boom")));
        results.Add(await mediator.Send(new Launch("rocket")));
        await mediator.Publish(new Notice("n"));

        var hidden = await Record.ExceptionAsync(() => mediator.Send(new Hidden("h")));
        results.Add("hidden:" + hidden?.GetType().Name);

        results.AddRange(provider.GetRequiredService<Trace>().Entries);
        return results;
    }

    [Fact]
    public async Task Generated_registration_matches_reflection_registration()
    {
        using var reflection = Build(generated: false);
        using var generated = Build(generated: true);

        var expected = await RunScenario(reflection);
        var actual = await RunScenario(generated);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Pipeline_runs_behaviors_processors_and_handler_in_order()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping("a"));

        Assert.Equal("Pong a", response);
        Assert.Equal(
            [
                "ping-only:before", "outer:before", "inner:before",
                "pre:Ping", "handler:Ping", "post:Ping:Pong a",
                "inner:after", "outer:after", "ping-only:after",
            ],
            provider.GetRequiredService<Trace>().Entries);
    }

    [Fact]
    public async Task Value_type_void_and_open_generic_requests_are_dispatched()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Equal(2, await sender.Send(new Count(1)));
        Assert.Equal(7, await sender.Send(new Echo<int>(7)));
        Assert.Equal("text", await sender.Send(new Echo<string>("text")));
        await sender.Send(new Fire("void"));

        Assert.Contains("handler:Fire:void", provider.GetRequiredService<Trace>().Entries);
    }

    [Fact]
    public async Task Constrained_open_behavior_only_applies_to_matching_requests()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await sender.Send(new Launch("rocket"));
        await sender.Send(new Count(1));

        var commandOnly = provider.GetRequiredService<Trace>().Entries.Where(entry => entry.StartsWith("command-only:", StringComparison.Ordinal));
        Assert.Equal(["command-only:Launch"], commandOnly);
    }

    [Fact]
    public async Task Exception_handler_recovers_the_response()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Explode("boom"));

        Assert.Equal("recovered", response);
        Assert.Contains("recovered:boom", provider.GetRequiredService<Trace>().Entries);
    }

    [Theory]
    [InlineData(NotificationPublishStrategy.Sequential)]
    [InlineData(NotificationPublishStrategy.Parallel)]
    public async Task Notifications_reach_closed_and_open_generic_handlers(NotificationPublishStrategy strategy)
    {
        using var provider = Build(generated: true, options => options.NotificationPublishStrategy = strategy);
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new Notice("n"));

        var entries = provider.GetRequiredService<Trace>().Entries;
        if (strategy == NotificationPublishStrategy.Sequential)
        {
            // Full-name order of the declaring types, as with assembly scanning.
            Assert.Equal(["notice:A", "notice:B", "audit:Notice"], entries);
        }
        else
        {
            Assert.Equal(["audit:Notice", "notice:A", "notice:B"], entries.OrderBy(entry => entry, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Assembly_filter_excludes_handlers()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();

        await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Hidden("h")));
    }

    [Fact]
    public void Handlers_use_the_default_lifetime()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorGenerated(options =>
        {
            options.RegisterAssembly(typeof(Ping).Assembly);
            options.DefaultLifetime = ServiceLifetime.Transient;
        });

        var handler = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRequestHandler<Ping, string>));
        Assert.Equal(ServiceLifetime.Transient, handler.Lifetime);
        Assert.Equal(typeof(PingHandler), handler.ImplementationType);

        var closedOpenGeneric = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRequestHandler<Echo<int>, int>));
        Assert.Equal(typeof(EchoHandler<int>), closedOpenGeneric.ImplementationType);
    }

    [Fact]
    public async Task Without_RegisterAssembly_no_handler_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediatorGenerated();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IRequestHandler<Ping, string>));
        await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping("a")));
    }

#pragma warning disable SMG003 // Intentional: exercises a request type the generator cannot see.
    private static Task<T> SendEcho<T>(ISender sender, T value) => sender.Send(new Echo<T>(value));
#pragma warning restore SMG003

    [Fact]
    public async Task Request_unknown_at_compile_time_fails_with_guidance_instead_of_using_reflection()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();

        var exception = await Assert.ThrowsAsync<RequestHandlerResolutionException>(
            () => SendEcho(scope.ServiceProvider.GetRequiredService<ISender>(), Guid.NewGuid()));

        Assert.Contains("source generator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Directly_constructed_mediator_uses_the_generated_table()
    {
        using var provider = Build(generated: true);
        using var scope = provider.CreateScope();
        var mediator = new Mediator(scope.ServiceProvider);

        Assert.Equal(2, await mediator.Send(new Count(1)));
        Assert.Equal(5, await mediator.Send(new Echo<int>(5)));
        await mediator.Send(new Fire("direct"));

        // A reflection fallback would dispatch this; the generated table rejects it.
        var exception = await Assert.ThrowsAsync<RequestHandlerResolutionException>(() => SendEcho(mediator, Guid.NewGuid()));
        Assert.Contains("source generator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manually_registered_handlers_dispatch_through_the_generated_table()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Trace>();
        services.AddSimpleMediatorGenerated();
        services.AddScoped<IRequestHandler<Count, int>, CountHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = new Mediator(scope.ServiceProvider);

        Assert.Equal(8, await mediator.Send(new Count(7)));
        Assert.Equal(["handler:Count"], provider.GetRequiredService<Trace>().Entries);
        await Assert.ThrowsAsync<RequestHandlerResolutionException>(() => SendEcho(mediator, Guid.NewGuid()));
    }

    [Fact]
    public void Assembly_not_scanned_by_the_generator_is_rejected()
    {
        var services = new ServiceCollection();
        var runtimeAssembly = Assembly.Load(new AssemblyName("System.Linq"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GeneratedMediatorRegistration.Register(
                services,
#pragma warning disable SMG006 // Intentional: an assembly only known at runtime.
                options => options.RegisterAssembly(runtimeAssembly),
#pragma warning restore SMG006
                _ => { }));

        Assert.Contains("was not scanned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Behavior_not_known_to_the_generator_is_rejected()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GeneratedMediatorRegistration.Register(services, options => options.AddBehavior(typeof(OuterBehavior<,>)), _ => { }));

        Assert.Contains("not known to the SimpleMediator source generator", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOnBuild_rejects_a_duplicate_manual_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Trace>();
        services.AddScoped<IRequestHandler<Ping, string>>(provider => new PingHandler(provider.GetRequiredService<Trace>()));

        Assert.Throws<RequestHandlerResolutionException>(() => services.AddSimpleMediatorGenerated(options =>
        {
            options.RegisterAssembly(typeof(Ping).Assembly);
            options.ValidateOnBuild = true;
        }));
    }
}
