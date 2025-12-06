using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;
using SimpleMediator.ConsoleTest;

var services = new ServiceCollection();

// Register SimpleMediator with options
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>));
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

var serviceProvider = services.BuildServiceProvider();

var mediator = serviceProvider.GetRequiredService<IMediator>();

Console.WriteLine("--- Testing Request/Response ---");
var pingResponse = await mediator.Send(new PingRequest { Message = "Hello World" });
Console.WriteLine($"Response: {pingResponse}");

var response = await mediator.Send(new Request { RequestMessage = "Hello World" });
Console.WriteLine($"Response: {response.ResponseMessage}");

Console.WriteLine("\n--- Testing Void Request ---");
await mediator.Send(new PrintRequest { Text = "This is a void request" });

Console.WriteLine("\n--- Testing Notification ---");
await mediator.Publish(new PingNotification { Message = "Hello Notification" });



Console.WriteLine("\nDone!");
