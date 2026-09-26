using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.ConsoleSample;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

// Native AOT smoke test: the console sample's handlers, registered by the source generator instead
// of runtime assembly scanning. Every result is asserted, so CI fails on any behavioral regression
// of the native binary, not only on a crash.
var services = new ServiceCollection();

services.AddSimpleMediatorGenerated(options =>
{
    options.RegisterAssembly(typeof(PingRequest).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>));
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var failures = 0;

Check("reference-type response", await mediator.Send(new PingRequest { Message = "AOT" }), "Pong: AOT");
Check("class response", (await mediator.Send(new Request { RequestMessage = "AOT" })).ResponseMessage, "AOT");

await mediator.Send(new PrintRequest { Text = "void request (Unit)" });
Check("void request (Unit)", "completed", "completed");

await mediator.Publish(new PingNotification { Message = "AOT notification" });
Check("notification", "published", "published");

Check("pre/post handlers", await mediator.Send(new PrePostRequest { Message = "AOT" }), "Handled: AOT");
Check("open-generic handler over a value type", await mediator.Send(new EchoRequest<int>(42)), 42);
Check("open-generic handler over a reference type", await mediator.Send(new EchoRequest<string>("generic")), "generic");
Check("exception handler", await mediator.Send(new FaultyRequest("boom")), "Recovered from: handler blew up on 'boom'");

// A Mediator constructed directly must take the generated table from the container it is given.
var direct = new Mediator(scope.ServiceProvider);
Check("direct mediator, value type", await direct.Send(new EchoRequest<int>(7)), 7);
await direct.Send(new PrintRequest { Text = "direct mediator, void request (Unit)" });

// Manually registered handlers: the parameterless overload registers only the dispatch table.
var manualServices = new ServiceCollection();
manualServices.AddSimpleMediatorGenerated();
manualServices.AddTransient<IRequestHandler<EchoRequest<int>, int>, EchoHandler<int>>();
using (var manualProvider = manualServices.BuildServiceProvider())
using (var manualScope = manualProvider.CreateScope())
{
    Check("manual registration, value type", await new Mediator(manualScope.ServiceProvider).Send(new EchoRequest<int>(9)), 9);
}

// Without any SimpleMediator registration reflection is the only option, which Native AOT cannot run:
// the constructor must refuse with guidance instead of failing later with NotSupportedException.
using (var emptyProvider = new ServiceCollection().BuildServiceProvider())
using (var emptyScope = emptyProvider.CreateScope())
{
    string outcome;
    try
    {
        _ = new Mediator(emptyScope.ServiceProvider);
        outcome = "constructed";
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("AddSimpleMediatorGenerated", StringComparison.Ordinal))
    {
        outcome = "rejected with guidance";
    }

    Check("direct mediator without registration", outcome, "rejected with guidance");
}

Console.WriteLine(failures == 0 ? "AOT smoke test passed." : $"AOT smoke test FAILED: {failures} check(s).");
return failures == 0 ? 0 : 1;

void Check<T>(string name, T actual, T expected)
{
    var passed = EqualityComparer<T>.Default.Equals(actual, expected);
    failures += passed ? 0 : 1;
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}: {actual}");
}
