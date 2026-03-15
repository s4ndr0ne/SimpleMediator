# SimpleMediator
A simple, no-frills implementation of the mediator pattern in .NET.
This is a study version aimed at exploring patterns and performance optimizations for microservices scenarios.

[![.NET](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml/badge.svg)](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml)

## Installation
This library is intended to be used as a NuGet package. To install it, use the .NET CLI:
```bash
dotnet add package s4ndr0ne.SimpleMediator
```

## Getting Started

### 1. Dependency Injection
First, you need to register SimpleMediator with your service provider. In your `Program.cs` or `Startup.cs`, call the `AddSimpleMediator` extension method.

You need to tell the mediator which assemblies contain your handlers. You can also configure other options, like pipeline behaviors and the default service lifetime.

```csharp
using SimpleMediator;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSimpleMediator(options =>
{
    // Scan the assembly containing your handlers
    options.RegisterAssembly(typeof(Program).Assembly);
    
    // Optionally add pipeline behaviors
    // options.AddBehavior(typeof(YourPipelineBehavior<,>));

    // Optionally change the default lifetime (default is Transient)
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

var serviceProvider = services.BuildServiceProvider();
```

## Usage
Once configured, you can inject `IMediator` into your services and start sending requests or publishing notifications.

### Request/Response Messages
A request is a message that is sent to a single handler and can return a response.

**1. Create a request and response**

A request must implement `IRequest<TResponse>` where `TResponse` is the type of the expected response. If a request does not return a value, you can use the non-generic `IRequest`.

```csharp
using SimpleMediator.Interfaces;

// Request with a response
public class PingRequest : IRequest<string>
{
    public string Message { get; set; }
}

// Request without a response
public class PrintRequest : IRequest
{
    public string Text { get; set; }
}
```

**2. Create a handler**

A handler must implement `IRequestHandler<TRequest, TResponse>` or `IRequestHandler<TRequest>` for requests that don't return a value.

```csharp
using SimpleMediator.Interfaces;

// Handler for PingRequest
public class PingRequestHandler : IRequestHandler<PingRequest, string>
{
    public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Pong: {request.Message}");
    }
}

// Handler for PrintRequest
public class PrintRequestHandler : IRequestHandler<PrintRequest>
{
    public Task Handle(PrintRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine(request.Text);
        return Task.CompletedTask;
    }
}
```
*SimpleMediator will automatically discover and register these handlers from the assemblies you specified during setup.*

**3. Send a request**

Inject `IMediator` and use the `Send` method.

```csharp
public class MyService
{
    private readonly IMediator _mediator;

    public MyService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task DoWork()
    {
        // Send a request with a response
        var response = await _mediator.Send(new PingRequest { Message = "Hello" });
        Console.WriteLine(response); // Output: Pong: Hello

        // Send a request without a response
        await _mediator.Send(new PrintRequest { Text = "Fire and forget" });
    }
}
```

### Notifications
A notification is a message that is sent to multiple handlers (zero or more). It's a fire-and-forget mechanism and does not return a value.

**1. Create a notification**

A notification must implement the `INotification` marker interface.

```csharp
using SimpleMediator.Interfaces;

public class MyNotification : INotification
{
    public string Message { get; set; }
}
```

**2. Create handlers**

You can create multiple handlers for a single notification. Each handler must implement `INotificationHandler<TNotification>`.

```csharp
using SimpleMediator.Interfaces;

public class EmailHandler : INotificationHandler<MyNotification>
{
    public Task Handle(MyNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Sending email: {notification.Message}");
        return Task.CompletedTask;
    }
}

public class SmsHandler : INotificationHandler<MyNotification>
{
    public Task Handle(MyNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Sending SMS: {notification.Message}");
        return Task.CompletedTask;
    }
}
```

**3. Publish a notification**

Inject `IMediator` and use the `Publish` method.

```csharp
public class MyService
{
    private readonly IMediator _mediator;

    public MyService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public async Task DoWork()
    {
        await _mediator.Publish(new MyNotification { Message = "Important event happened!" });
    }
}
// This will invoke both EmailHandler and SmsHandler
```

### Pipeline Behaviors
Pipeline behaviors allow you to add cross-cutting concerns to your request handlers. They can execute code before and after a request is handled. A common use case is logging or validation.

**1. Create a behavior**

A behavior must implement `IPipelineBehavior<TRequest, TResponse>`. It must be a generic class.

```csharp
using SimpleMediator.Interfaces;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Behavior] Handling {typeof(TRequest).Name}");
        var response = await next();
        Console.WriteLine($"[Behavior] Handled {typeof(TRequest).Name}");
        return response;
    }
}
```

**2. Register the behavior**

Register your behavior in the `AddSimpleMediator` configuration. Behaviors are processed in the order they are added.

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>)); // Register the open generic type
});
```

Now, every time you send a request that returns a value, the `LoggingBehavior` will be executed.

### Pre / Post Request Handlers
SimpleMediator also supports lightweight pre- and post-request handlers that run around the main request handler. They are useful for tasks such as metrics, lightweight auditing, or context enrichment that don't require wrapping the whole pipeline.

- `IPreRequestHandler<TRequest, TResponse>`: executed before the main `IRequestHandler` with signature `Task Handle(TRequest request, CancellationToken)`.
- `IPostRequestHandler<TRequest, TResponse>`: executed after the main `IRequestHandler` with signature `Task Handle(TRequest request, TResponse response, CancellationToken)`.

These are discovered automatically by `AddSimpleMediator` when you register assemblies. They execute inside the innermost delegate (i.e., after pipeline behaviors have been applied and before/after the actual handler is invoked).

Example:

```csharp
public class AuditPreHandler : IPreRequestHandler<MyRequest, MyResponse>
{
    public Task Handle(MyRequest request, CancellationToken cancellationToken)
    {
        // record request metadata
        return Task.CompletedTask;
    }
}

public class AuditPostHandler : IPostRequestHandler<MyRequest, MyResponse>
{
    public Task Handle(MyRequest request, MyResponse response, CancellationToken cancellationToken)
    {
        // record response/result metadata
        return Task.CompletedTask;
    }
}
```

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Contributing
Contributions, pull requests, and corrections are welcome. Please open issues or submit PRs to propose improvements.
