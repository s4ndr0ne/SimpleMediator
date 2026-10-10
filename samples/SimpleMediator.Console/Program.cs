using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SimpleMediator;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;
using SimpleMediator.ConsoleSample;

var builder = Host.CreateApplicationBuilder(args);
var services = builder.Services;

services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>));
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

// This in-memory store and serializer are for demonstration only. A production store must persist
// messages in the same database transaction as the request's business changes.
services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
services.AddSingleton<IOutboxSerializer, SampleOutboxSerializer>();
services.AddSingleton<OutboxDemoSignal>();
services.AddSimpleMediatorOutbox(options => options.PollingInterval = TimeSpan.FromMilliseconds(50));

using var host = builder.Build();
await host.StartAsync();

// Resolve the mediator from a scope so handlers and the outbox publisher share scoped services.
await using var scope = host.Services.CreateAsyncScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

Console.WriteLine("--- Testing Request/Response ---");
var pingResponse = await mediator.Send(new PingRequest { Message = "Hello World" });
Console.WriteLine($"Response: {pingResponse}");

var response = await mediator.Send(new Request { RequestMessage = "Hello World" });
Console.WriteLine($"Response: {response.ResponseMessage}");

Console.WriteLine("\n--- Testing Void Request ---");
await mediator.Send(new PrintRequest { Text = "This is a void request" });

Console.WriteLine("\n--- Testing Notification ---");
await mediator.Publish(new PingNotification { Message = "Hello Notification" });

Console.WriteLine("\n--- Testing Pre/Post Handlers ---");
var prePostResponse = await mediator.Send(new PrePostRequest { Message = "FromConsole" });
Console.WriteLine($"PrePost Response: {prePostResponse}");

Console.WriteLine("\n--- Testing Open-Generic Handler ---");
var echoInt = await mediator.Send(new EchoRequest<int>(42));
Console.WriteLine($"Echo<int>: {echoInt}");
var echoString = await mediator.Send(new EchoRequest<string>("hello generics"));
Console.WriteLine($"Echo<string>: {echoString}");

Console.WriteLine("\n--- Testing Exception Handler ---");
var recovered = await mediator.Send(new FaultyRequest("boom"));
Console.WriteLine($"Faulty Response: {recovered}");

Console.WriteLine("\n--- Testing Outbox (in-memory demo) ---");
var outboxSignal = scope.ServiceProvider.GetRequiredService<OutboxDemoSignal>();
var orderId = await mediator.Send(new CreateOrderRequest("Sample order"));
Console.WriteLine($"Created order: {orderId}");
await outboxSignal.Delivered.WaitAsync(TimeSpan.FromSeconds(5));

Console.WriteLine("\nDone!");
await host.StopAsync();
