# SimpleMediator
A lightweight, high-performance implementation of the mediator pattern in .NET, optimized for microservices and high-throughput scenarios.

SimpleMediator is designed to be fast, reliable, and "DI-friendly", following modern .NET practices like correct scope management and minimal reflection overhead.

[![.NET](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml/badge.svg)](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml)

## Core Features & Optimizations

- **🚀 High Performance Dispatch**: Uses cached **Compiled Expression Trees** (MSIL) for mediator wrapper dispatch, while handler instances are still resolved correctly through Microsoft Dependency Injection.
- **🛡️ Native Scope Support**: Correctly respects the surrounding Dependency Injection scope. Scoped services (like `DbContext` or `UnitOfWork`) are shared correctly between your controllers and handlers.
- **⚡ Configurable Notification Dispatch**: Notification handlers run **sequentially by default** — safe to share a scoped service (like `DbContext`) across handlers — and can opt into parallel execution via `Task.WhenAll` when handlers are independent.
- **🔗 Advanced Pipeline**: Supports `IPipelineBehavior`, `IPreRequestHandler`, `IPostRequestHandler`, and `IRequestExceptionHandler`, with ordering and open generics — including **open-generic request handlers** for generic requests.
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
Notifications are broadcast messages sent to every registered handler.

```csharp
// 1. Define Notification
public record UserCreated(string Email) : INotification;

// 2. Multiple Handlers
public class WelcomeEmailHandler : INotificationHandler<UserCreated> { ... }
public class AnalyticsHandler : INotificationHandler<UserCreated> { ... }

// 3. Publish
await mediator.Publish(new UserCreated("user@example.com"));
```

#### Dispatch strategy
By default handlers run **sequentially** (`NotificationPublishStrategy.Sequential`). This is the safe choice: all handlers share the same DI scope, so a scoped, non-thread-safe service (e.g. `DbContext`) is never touched concurrently. If a handler throws, the remaining handlers are not invoked.

Opt into parallel dispatch only when handlers are independent:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
});
```

In `Parallel` mode handlers run via `Task.WhenAll`; if more than one fails, an `AggregateException` carrying **all** failures is thrown (not just the first).

> **Notification matching is exact, not contravariant.** Although `INotificationHandler<in TNotification>` is declared contravariant, Microsoft DI resolves handlers by the exact closed type that is published. A handler registered as `INotificationHandler<INotification>` (or for any base type) will **not** receive derived concrete notifications — register handlers for the concrete notification type you publish.

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

Register behaviors via `AddBehavior`. Execution order is controlled by each behavior's `Order` property (lower runs first / outermost), not by registration order. You can register an open generic type or a closed type bound to a specific request/response pair:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>)); // open generic, applies to every request
    options.AddBehavior(typeof(MySpecificBehavior)); // closed, implements IPipelineBehavior<MyRequest, MyResponse>
});
```

### Pre / Post Request Handlers
Lightweight hooks that run *inside* the behavior pipeline, right before or after the main handler.

- `IPreRequestHandler<TRequest, TResponse>`: `Task Handle(TRequest request, CancellationToken)`
- `IPostRequestHandler<TRequest, TResponse>`: `Task Handle(TRequest request, TResponse response, CancellationToken)`

### Open-Generic Request Handlers
A single handler can serve a generic request for every closed type argument. Both the request and the handler are open generics:

```csharp
public record EchoRequest<T>(T Value) : IRequest<T>;

public class EchoHandler<T> : IRequestHandler<EchoRequest<T>, T>
{
    public Task<T> Handle(EchoRequest<T> request, CancellationToken ct) => Task.FromResult(request.Value);
}

// Discovered automatically by RegisterAssembly — no explicit registration needed.
int n   = await mediator.Send(new EchoRequest<int>(42));      // -> 42
string s = await mediator.Send(new EchoRequest<string>("hi")); // -> "hi"
```

The handler is closed to the concrete request type on first use (the match and its construction factory are cached), and its constructor dependencies are injected from the current DI scope. The one-handler-per-request rule still applies: if both a closed and an open-generic handler match the same request, `Send` throws.

> **Lifetime:** open-generic request handlers are **created per request** (effectively transient), regardless of `DefaultLifetime`. The resolution *plan* is cached, never the instance, so injected scoped dependencies remain correct. If you need a specific lifetime for the handler itself, register a closed handler instead.

> **Matcher scope:** type-argument inference covers the common shapes — direct parameters (`IRequestHandler<Query<T>, Result<T>>`), nested generics, and single-dimension arrays (`IRequestHandler<ArrayRequest<T>, T[]>`). It is a deliberately simplified unifier; exotic signatures (multi-dimensional arrays, by-ref/pointer types, deeply mixed constructions) may not resolve. When in doubt, register a closed handler — and turn on [startup validation](#startup-validation) to catch a request that ends up with no matching handler early.

### Exception Handlers
Recover from (or observe) exceptions thrown anywhere in a request's pipeline — the handler, its pre/post handlers, or any behavior.

```csharp
public class ValidationExceptionHandler : IRequestExceptionHandler<CreateUser, UserResult>
{
    public Task Handle(CreateUser request, Exception exception,
        RequestExceptionHandlerState<UserResult> state, CancellationToken ct)
    {
        if (exception is ValidationException) state.SetHandled(UserResult.Invalid()); // swallow + substitute
        return Task.CompletedTask; // leaving it un-handled rethrows the original exception
    }
}
```

Handlers run in **ascending `Order`** (the `IRequestExceptionHandler<,>.Order` property, default `0`); the first to call `SetHandled` supplies the response returned to the caller and short-circuits the rest. If none handles the exception, it is rethrown with its original stack trace. A **catch-all** handler is just an open generic — `class LogExceptions<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse>` — and is picked up automatically by assembly scanning.

> **Cancellation is never swallowed:** an `OperationCanceledException` is treated as control flow, not as an error — it is *never* offered to `IRequestExceptionHandler<,>` and propagates straight to the caller, regardless of whether the cancellation originated from the request's own `CancellationToken` or from a linked/alien token a behavior or handler observed. Likewise, when notification handlers run in `Parallel` and every faulted handler throws `OperationCanceledException` while the supplied token is cancelled, `Publish` surfaces the `OperationCanceledException` itself rather than an `AggregateException` wrapping it.

## Startup Validation
Configuration mistakes (two handlers for one request, or a request matched by both a closed and an open-generic handler) otherwise surface only on the first call that hits them. Opt into fail-fast validation so a misconfigured app dies at startup instead of in production:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.ValidateOnBuild = true; // throws from AddSimpleMediator on a bad configuration
});

// …or validate explicitly, anywhere after registration:
services.ValidateSimpleMediator();
```

Validation flags more than one registration for the same closed `IRequestHandler<,>` (whether by type, factory, or instance) and any request matched by both a closed and an open-generic handler.

> **Modular registration:** `AddSimpleMediator` may be called more than once — e.g. once per module. Closed handlers accumulate, and open-generic handlers are merged across calls. `NotificationPublishStrategy` and `ValidateOnBuild` follow a **last-call-wins** rule, so set them consistently (or only once) if you split registration across modules.

## Observability
SimpleMediator keeps the core dependency-free; cross-cutting concerns like logging, metrics, tracing, and correlation IDs are implemented as ordinary pipeline behaviors. A timing + tracing behavior, for example:

```csharp
public class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private static readonly ActivitySource Source = new("SimpleMediator");
    private readonly ILogger<TracingBehavior<TRequest, TResponse>> _logger;

    public TracingBehavior(ILogger<TracingBehavior<TRequest, TResponse>> logger) => _logger = logger;

    public int Order => 0; // outermost: wraps everything else

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        using var activity = Source.StartActivity(typeof(TRequest).Name); // OpenTelemetry span
        var sw = Stopwatch.StartNew();
        try
        {
            return await next(ct);
        }
        finally
        {
            _logger.LogInformation("{Request} handled in {Elapsed}ms", typeof(TRequest).Name, sw.ElapsedMilliseconds);
        }
    }
}

// services.AddSimpleMediator(o => o.AddBehavior(typeof(TracingBehavior<,>)));
```

The same shape covers metrics (increment counters), correlation IDs (read/propagate from the request or an ambient context), and structured error logging (log in a `catch` before rethrowing, or use an `IRequestExceptionHandler<,>`).

## AOT & Trimming
SimpleMediator relies on assembly scanning, `Expression.Compile`, runtime `MakeGenericType`, and `ActivatorUtilities`. It targets classic (JIT) hosts such as ASP.NET Core and is **not currently Native-AOT or trimming-safe** — `AddSimpleMediator` is annotated with `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so trim/AOT builds will surface warnings. Do not enable `PublishTrimmed`/`PublishAot` for apps that use it without your own verification.

## Why SimpleMediator?

SimpleMediator uses a **hybrid approach**:
1. **Discovery**: Reflection is used once at startup to find handlers.
2. **Compilation**: The first time a request or notification type is used, an **Expression Tree** is compiled into a cached wrapper factory.
3. **Execution**: Subsequent calls reuse the cached wrapper factory, while actual handlers and pipeline services are resolved through Microsoft Dependency Injection so lifetimes and scopes remain correct.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Contributing
Contributions, pull requests, and corrections are welcome. Please open issues or submit PRs to propose improvements.
