# SimpleMediator
A lightweight, high-performance implementation of the mediator pattern in .NET, optimized for microservices and high-throughput scenarios.

SimpleMediator is designed to be fast, reliable, and "DI-friendly", following modern .NET practices like correct scope management and minimal reflection overhead.

[![.NET](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml/badge.svg)](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml)

## Core Features & Optimizations

- **🚀 Extreme Performance**: Uses **Compiled Expression Trees** (MSIL) for handler instantiation. Unlike standard reflection-based mediators, SimpleMediator compiles factories at runtime, making execution nearly as fast as native code.
- **🛡️ Native Scope Support**: Correctly respects the surrounding Dependency Injection scope. Scoped services (like `DbContext` or `UnitOfWork`) are shared correctly between your controllers and handlers.
- **⚡ Parallel Notifications**: Notification handlers are executed in parallel via `Task.WhenAll`, maximizing throughput for event-driven logic.
- **🔗 Advanced Pipeline**: Supports `IPipelineBehavior`, `IPreRequestHandler`, and `IPostRequestHandler` with support for ordering and open generics.
- **📦 Zero Dependencies**: Built strictly on top of `Microsoft.Extensions.DependencyInjection`.

## Installation
This library is intended to be used as a NuGet package. To install it, use the .NET CLI:
```bash
dotnet add package s4ndr0ne.SimpleMediator
```

## Getting Started

### 1. Dependency Injection
Register SimpleMediator in your `Program.cs` or `Startup.cs`.

```csharp
using SimpleMediator;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSimpleMediator(options =>
{
    // Scan assemblies for Handlers, Pre/Post Handlers, and Behaviors
    options.RegisterAssembly(typeof(Program).Assembly);
    
    // Optionally change the default lifetime (default is Scoped)
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

var serviceProvider = services.BuildServiceProvider();
```

## Usage

### Request/Response
Requests are point-to-point messages that return a result.

```csharp
// 1. Define Request
public record PingRequest(string Message) : IRequest<string>;

// 2. Define Handler
public class PingRequestHandler : IRequestHandler<PingRequest, string>
{
    public Task<string> Handle(PingRequest request, CancellationToken ct) 
        => Task.FromResult($"Pong: {request.Message}");
}

// 3. Send via Mediator
var response = await mediator.Send(new PingRequest("Hello"));
```

### Notifications
Notifications are broadcast messages sent to multiple handlers in parallel.

```csharp
// 1. Define Notification
public record UserCreated(string Email) : INotification;

// 2. Multiple Handlers (executed in parallel)
public class WelcomeEmailHandler : INotificationHandler<UserCreated> { ... }
public class AnalyticsHandler : INotificationHandler<UserCreated> { ... }

// 3. Publish
await mediator.Publish(new UserCreated("user@example.com"));
```

### Pipeline Behaviors
Behaviors allow you to wrap requests with cross-cutting concerns (Logging, Validation, Caching).

```csharp
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public int Order => 1; // Control execution order

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        Console.WriteLine($"Handling {typeof(TRequest).Name}");
        return await next(ct);
    }
}
```

### Pre / Post Request Handlers
Lightweight hooks that run *inside* the behavior pipeline, right before or after the main handler.

- `IPreRequestHandler<TRequest, TResponse>`: `Task Handle(TRequest request, CancellationToken)`
- `IPostRequestHandler<TRequest, TResponse>`: `Task Handle(TRequest request, TResponse response, CancellationToken)`

## Why SimpleMediator?

Most mediator implementations rely on `Activator.CreateInstance` or heavy reflection at every call. SimpleMediator uses a **hybrid approach**:
1. **Discovery**: Reflection is used once at startup to find handlers.
2. **Compilation**: The first time a request is sent, an **Expression Tree** is compiled into a native factory.
3. **Execution**: Subsequent calls use the compiled factory and a cached delegate chain, providing near-native performance.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Contributing
Contributions, pull requests, and corrections are welcome. Please open issues or submit PRs to propose improvements.
