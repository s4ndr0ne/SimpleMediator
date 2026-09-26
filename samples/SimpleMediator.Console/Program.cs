using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;
using SimpleMediator.ConsoleSample;

var services = new ServiceCollection();

// Register SimpleMediator with options
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>));
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

var serviceProvider = services.BuildServiceProvider();

// A mediator is always resolved from a scope. Resolving it from the root provider is rejected
// (SimpleMediatorOptions.RequireScopedMediator), because a root-owned mediator would turn every
// scoped dependency into a process-wide instance.
using var scope = serviceProvider.CreateScope();
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

Console.WriteLine("\nDone!");
