# SimpleMediator
A lightweight implementation of the mediator pattern for .NET, built on Microsoft.Extensions.DependencyInjection.

SimpleMediator focuses on predictable behaviour rather than raw speed: correct DI scope handling, explicit failure contracts, and fail-fast validation. Dispatch overhead is small but it is a reflection-plus-DI design, not a source-generated one; see [What the performance actually consists of](#what-the-performance-actually-consists-of).

[![.NET](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml/badge.svg)](https://github.com/s4ndr0ne/SimpleMediator/actions/workflows/dotnet.yml)
[![GitHub](https://img.shields.io/badge/GitHub-s4ndr0ne%2FSimpleMediator-181717?logo=github)](https://github.com/s4ndr0ne/SimpleMediator)
[![NuGet](https://img.shields.io/nuget/v/s4ndr0ne.SimpleMediator?logo=nuget)](https://www.nuget.org/packages/s4ndr0ne.SimpleMediator)
[![NuGet Downloads](https://img.shields.io/nuget/dt/s4ndr0ne.SimpleMediator?logo=nuget)](https://www.nuget.org/packages/s4ndr0ne.SimpleMediator)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)

## Core Features & Optimizations

- **🚀 Cached Dispatch**: One typed wrapper per request/notification type is created on first use and cached, so steady-state dispatch involves no reflection; handlers, behaviors and pre/post handlers are then resolved through Microsoft DI on every call so lifetimes stay correct.
- **🛡️ Scope Correctness**: Scoped services (like `DbContext` or `UnitOfWork`) are shared correctly between your controllers and handlers, and resolving the mediator from the root provider — which would silently turn every one of them into a process-wide singleton — throws `MediatorScopeException` when it is resolved. See [Resolve the mediator from a scope](#2-resolve-the-mediator-from-a-scope-not-from-the-root).
- **✂️ Segregated interfaces**: depend on `ISender` (requests) or `IPublisher` (notifications) instead of the full `IMediator`.
- **⚡ Configurable Notification Dispatch**: Notification handlers run **sequentially by default** — safe to share a scoped service (like `DbContext`) across handlers — and can opt into parallel execution via `Task.WhenAll` when handlers are independent.
- **🔗 Advanced Pipeline**: Supports `IPipelineBehavior`, `IPreRequestHandler`, `IPostRequestHandler`, and `IRequestExceptionHandler`, with ordering and open generics — including **open-generic request handlers** for generic requests.
- **📦 Minimal Dependencies**: Built on top of `Microsoft.Extensions.DependencyInjection.Abstractions`.

## Installation
This library is intended to be used as a NuGet package. To install it, use the .NET CLI:
```bash
dotnet add package s4ndr0ne.SimpleMediator
```

## Supported DI container

SimpleMediator supports **only Microsoft.Extensions.DependencyInjection** (the default container of
ASP.NET Core, the Generic Host, Azure Functions and Worker Services). Every behaviour documented here
— scope handling, the root-mediator guard, open-generic resolution and constraint filtering, the
order in which `GetServices<T>()` returns handlers and behaviors, disposal of custom-mapped handlers —
is implemented and tested against that container only.

Third-party containers plugged in through `IServiceProviderFactory` (Autofac, Lamar, DryIoc,
SimpleInjector, …) are **not supported**. They may appear to work, but known differences include:

- the root-mediator guard relies on how Microsoft DI resolves `IServiceScopeFactory`; on another
  container it silently never fires, so a root-owned mediator is no longer detected;
- enumeration order, open-generic constraint handling and disposal semantics differ between
  containers, which changes behavior ordering and "one handler per request" detection.

If you must use another container, keep the mediator and its handlers in a Microsoft DI
`IServiceCollection` and validate the behaviours your application relies on with your own tests.

## Getting Started

### 1. Dependency Injection
Register SimpleMediator in your `Program.cs` or `Startup.cs`.

```csharp
using SimpleMediator;
using SimpleMediator.Interfaces;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSimpleMediator(options =>
{
    // Scan for request, notification, pre/post, and exception handlers.
    options.RegisterAssembly(typeof(Program).Assembly);
    // Register pipeline behaviors explicitly with AddBehavior.

    // Optionally change the default lifetime (default is Scoped)
    options.DefaultLifetime = ServiceLifetime.Scoped;
});

var serviceProvider = services.BuildServiceProvider();
```

### 2. Resolve the mediator from a scope (not from the root)

This is a **hard requirement**, not a style preference.

`IMediator` resolves every handler, pre/post handler, behavior, and exception handler from the
provider it was created with. A mediator created from the **root** provider therefore resolves
scoped services — your `DbContext`, unit of work, tenant context, current-user accessor — from the
root, where MS DI treats them as one process-wide instance shared by every concurrent request. The
failure is silent: nothing throws, you just get a non-thread-safe context serving all traffic.

```csharp
// Correct: one scope per HTTP request (ASP.NET Core does this for you when you inject IMediator
// into a controller or minimal-API endpoint).
using var scope = serviceProvider.CreateScope();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
await mediator.Send(new PingRequest("Hello"));
```

Resolving `IMediator`, `ISender` or `IPublisher` from the root provider throws
`MediatorScopeException` at the moment it is resolved. For a singleton that injects the mediator
this happens when the singleton is first constructed (at startup if it is created eagerly, e.g. a
hosted service), so the mistake surfaces as an exception instead of as shared state under load.
The guard covers both resolution through DI and a `Mediator` constructed by hand with
`new Mediator(rootProvider)`. To accept the
root-owned trade-off deliberately — only correct when *every* handler dependency is a singleton —
set:

```csharp
services.AddSimpleMediator(options => options.RequireScopedMediator = false);
```

#### Long-lived components: background services, hosted services, queue consumers

Inject `IServiceScopeFactory` (not `IMediator`) and create a scope per unit of work. The
`CreateMediatorScope()` helper owns the scope and the mediator together so they cannot drift apart:

```csharp
public sealed class OrderConsumer(IServiceScopeFactory scopeFactory, ILogger<OrderConsumer> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var order in ReadOrdersAsync(stoppingToken))
        {
            // One scope per message: scoped dependencies are created and released with it.
            await using var mediatorScope = scopeFactory.CreateMediatorScope();
            await mediatorScope.Mediator.Send(new ProcessOrder(order), stoppingToken);
        }
    }
}
```

`CreateMediatorScope()` has overloads for both `IServiceProvider` and `IServiceScopeFactory`, and
the returned `IMediatorScope` exposes `Mediator` and `ServiceProvider` and implements both
`IDisposable` and `IAsyncDisposable`.

> **How the check works.** MS DI gives the root provider and every scope the same runtime type, so
> the discriminator is the scope factory: `IServiceScopeFactory` always resolves to the container's
> root scope. A service resolved *from* the root is handed that exact object as its
> `IServiceProvider`; the public root `ServiceProvider` (what `new Mediator(root)` receives) is not
> that object but resolves `IServiceProvider` to it. A caller-created scope matches neither. The
> comparison is advisory by design — a container that wires the scope factory differently simply
> does not trigger the guard instead of rejecting a valid mediator. This is one of the reasons only
> Microsoft DI is supported (see [Supported DI container](#supported-di-container)).


## Usage

### `ISender`, `IPublisher` and `IMediator`

`IMediator` is the union of two narrower interfaces:

| Interface | Members | Typical consumer |
|---|---|---|
| `ISender` | `Send<TResponse>(IRequest<TResponse>)`, `Send(IRequest)` | controllers, endpoints, application services |
| `IPublisher` | `Publish<TNotification>(TNotification)` | domain-event dispatchers, outbox relays |
| `IMediator : ISender, IPublisher` | all of the above | components that need both |

`AddSimpleMediator` registers all three as transient services. `ISender` and `IPublisher` forward to
the `IMediator` registration, so replacing `IMediator` (for example with a test double registered
before `AddSimpleMediator`) is honoured by all three. The same scope rules apply: resolve them inside
the request or operation scope.

```csharp
public sealed class OrdersController(ISender sender) : ControllerBase
{
    [HttpPost]
    public Task<OrderId> Create(CreateOrder command, CancellationToken ct) => sender.Send(command, ct);
}

public sealed class DomainEventDispatcher(IPublisher publisher)
{
    public async Task DispatchAsync(IEnumerable<INotification> events, CancellationToken ct)
    {
        foreach (var domainEvent in events)
        {
            await publisher.Publish(domainEvent, ct); // dispatched on the runtime type
        }
    }
}
```

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

> **AOT/trimming:** the public `ISender`/`IPublisher` (and therefore `IMediator`) and `Mediator` dispatch methods are annotated because
> wrapper creation uses reflection and runtime code generation. SimpleMediator currently targets
> JIT hosts; Native AOT and trimming are not supported without an application-specific verification
> strategy. See [AOT & Trimming](#aot--trimming) for what this means for a trimming-enabled build.

> **Request matching is exact.** Dispatch uses the request's concrete runtime type, so a handler registered for a base request does not handle a derived request. Although `IRequestHandler<in TRequest, TResponse>` is contravariant, the built-in DI lookup used by the mediator resolves the exact closed request type.

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
Publishing a notification with **no** registered handler is a silent no-op, not an error. By default handlers run **sequentially** (`NotificationPublishStrategy.Sequential`). This is the safe choice: all handlers share the same DI scope, so a scoped, non-thread-safe service (e.g. `DbContext`) is never touched concurrently. If a handler throws, the remaining handlers are not invoked. Every notification handler must return a non-null `Task`; returning `null` fails with `InvalidOperationException` in sequential mode and follows the same single-failure contract in parallel mode.

Opt into parallel dispatch only when handlers are independent:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.NotificationPublishStrategy = NotificationPublishStrategy.Parallel;
});
```

In `Parallel` mode handlers run via `Task.WhenAll`. If exactly one handler fails, its original exception is rethrown; if more than one fails, an `AggregateException` carrying **all** failures is thrown (not just the first). Cancellation retains its dedicated `OperationCanceledException` behavior.

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

Register behaviors via `AddBehavior`. Execution order is controlled by each behavior's `Order` property (lower runs first / outermost). Equal values run in registration order (FIFO): the first registered behavior is outermost and runs first, like MediatR and ASP.NET Core middleware. `Order` is a default interface member defaulting to `0`, so a behavior may omit it entirely; `IRequestExceptionHandler<,>.Order` has the same default, so both ordering contracts behave identically. Assembly scanning does not register behaviors; add each one explicitly. You can register an open generic type or a closed type bound to a specific request/response pair:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.AddBehavior(typeof(LoggingBehavior<,>)); // open generic, applies to every request
    options.AddBehavior(typeof(MySpecificBehavior)); // closed, implements IPipelineBehavior<MyRequest, MyResponse>
});
```

`Order` is read **once per request**, so it may depend on scoped state, but it must be *stable for
the duration of a single request*: the pipeline snapshots the values and then sorts, instead of
re-reading the property on every comparison. A behavior whose `Order` changes between reads cannot
produce an inconsistent sort.

Registering the same behavior type for the same request/response pair **twice with different
lifetimes** is reported as a configuration error rather than silently resolved. `TryAddEnumerable`
keeps the first registration and discards the second, which would otherwise make the effective
lifetime depend on module registration order.

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

There are two open-generic paths. A native-compatible handler lines its implementation parameters up 1:1 with the service contract:

```csharp
public class GenericHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, CancellationToken ct) => /* ... */;
}
```

Native-compatible handlers are registered as ordinary open-generic DI services. They follow `DefaultLifetime`, are disposed by the container, and behave exactly like closed handlers. Custom-mapped handlers such as `EchoHandler<T> : IRequestHandler<EchoRequest<T>, T>` cannot be closed by Microsoft DI, so they use SimpleMediator's type-argument matcher and are activated by SimpleMediator itself, outside the native registration path.

> **Lifetime of custom-mapped open-generic handlers.** These follow `DefaultLifetime` exactly like
> every other handler. Concretely:
>
> | `DefaultLifetime` | Where the instance lives | Built from | Disposed by |
> |---|---|---|---|
> | `Transient` (not the default) | one per `Send` | the current scope | SimpleMediator, at the end of the request |
> | `Scoped` (the default) | one per DI scope | the current scope | the scope |
> | `Singleton` | one per application | **the root provider** | the root provider |
>
> Only the *resolution plan* (the closed type plus its factory) is cached, never the instance, so
> scoped dependencies stay correct across requests.
>
> The `Singleton` row is the subtle one. A singleton custom-mapped handler is built once and reused
> forever, so it must not be built from a request scope: it would capture that scope's `DbContext`
> and keep handing out an instance whose scope was already disposed. SimpleMediator therefore builds
> singleton custom handlers from the **root** provider, and — because "the root scope's `DbContext`
> shared for the whole process" is almost never what an application wants — it **rejects** a singleton
> custom-mapped handler whose constructor takes a `Scoped` or `Transient` dependency:
>
> ```
> Open-generic request handler 'MyHandler<T>' is registered as a Singleton, but its constructor
> depends on 'AppDbContext', which is registered as Scoped. ... Use ServiceLifetime.Scoped
> (or Transient) for this handler, or register a closed handler instead.
> ```
>
> This check runs on every `AddSimpleMediator` call, not only under `ValidateOnBuild`. It reads
> lifetimes the way Microsoft DI resolves them:
>
> - a single dependency uses its **last** non-keyed registration; an `IEnumerable<T>` dependency is
>   rejected if **any** registration of `T` is `Scoped` or `Transient`;
> - a dependency closed over the handler's own type parameter (for example
>   `Handler<T>(IRepo<T> repo)` or `ILogger<Handler<T>>`) is checked against its open-generic
>   registration (`IRepo<>`, `ILogger<>`); with no open-generic registration its lifetime is
>   unknowable up front and the handler is rejected;
> - the constructor marked `[ActivatorUtilitiesConstructor]` is checked, otherwise every public
>   constructor.
>
> Only direct constructor dependencies are checked. Enable the host's `ValidateScopes` to catch a
> scoped service reached indirectly.
>
> Handler decoration is not supported by the single-handler resolver; use `IPipelineBehavior<,>` for
> cross-cutting concerns.


> **Matcher scope:** type-argument inference covers the common shapes — direct parameters (`IRequestHandler<Query<T>, Result<T>>`), nested generics, and single-dimension arrays (`IRequestHandler<ArrayRequest<T>, T[]>`). It is a deliberately simplified unifier; exotic signatures (multi-dimensional arrays, by-ref/pointer types, deeply mixed constructions) may not resolve. Unsupported open-generic mappings are rejected during registration. When in doubt, register a closed handler. Startup validation checks ambiguities for closed request types represented in the service registrations; it cannot predict every request type an application may send.

### Exception Handlers
Recover from (or observe) exceptions thrown anywhere in a request's processing — the handler, its pre/post handlers, any behavior, **and the construction of any of them**.

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

Handlers run in **ascending `Order`** (a default interface member, default `0`); equal values preserve DI resolution order. The value is read once per resolution, so it may depend on scoped state. The first handler to call `SetHandled` supplies the response returned to the caller and short-circuits the rest. If none handles the exception, it is rethrown with its original stack trace. A **catch-all** handler is just an open generic — `class LogExceptions<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse>` — and is picked up automatically by assembly scanning. Assembly scanning orders candidate types by `FullName`, so discovery is deterministic; still, assign distinct `Order` values when a fixed relative order matters.

If an exception handler itself throws, the mediator throws an `AggregateException` containing both the original request exception and the exception-handler failure.

#### Exactly what reaches an exception handler

This is worth being precise about, because a global catch-all is often used for logging and metrics
and silently misses some failures.

| Failure | Routed to `IRequestExceptionHandler<,>`? |
|---|---|
| The handler's `Handle` throws | yes |
| A pre-handler, post-handler, or behavior's `Handle` throws | yes |
| A behavior's `Handle` returns `null` | yes |
| **A behavior, pre/post handler, or request handler fails to be constructed** (missing dependency, bad configuration, throwing constructor) | **yes** |
| A handler returns `null` instead of a `Task` | yes, as an `InvalidOperationException` naming the handler |
| `OperationCanceledException` from anywhere | **no** — cancellation is control flow, never offered |
| No handler, or more than one handler, matches the request | **no** — see below |
| The exception handlers themselves cannot be constructed | **no** — the original exception wins |

**Handler selection is not a request failure.** `RequestHandlerResolutionException` (an
`InvalidOperationException`) is raised when no handler or more than one handler matches. It is
deliberately *not* offered to exception handlers: letting a catch-all observe it would hide a wiring
bug behind whatever substitute response the handler returns, and an exception handler that cannot
itself be constructed would replace a precise diagnostic with an unrelated DI error. The same
exception type is raised by the startup validators, so `ValidateOnBuild` and the first failing
request report identical wording.

**A broken exception pipeline never masks the request failure.** If the
`IRequestExceptionHandler<,>` instances cannot themselves be resolved, the original request
exception is rethrown rather than being replaced by the DI error — the caller still sees the thing
that actually went wrong.

> **Partial commits.** An exception handler can substitute a response for a request whose handler
> has *already* committed work (for example a `POST` handler succeeded and a post-handler then
> failed). SimpleMediator offers no transaction or outbox, and the substitution is not a rollback:
> the caller receives a "successful" shape while the write stands. Use a behavior that opens a
> transaction around `next(ct)`, or an outbox, if you need atomicity.


> **Cancellation is never swallowed:** an `OperationCanceledException` is treated as control flow, not as an error — it is *never* offered to `IRequestExceptionHandler<,>` and propagates straight to the caller, regardless of whether the cancellation originated from the request's own `CancellationToken` or from a linked/alien token a behavior or handler observed. Likewise, when notification handlers run in `Parallel` and every faulted handler throws `OperationCanceledException` while the supplied token is cancelled, `Publish` surfaces the `OperationCanceledException` itself rather than an `AggregateException` wrapping it.

## Startup Validation
Configuration mistakes involving a closed request registration (duplicate handlers, or a closed handler also matched by an open-generic handler) otherwise surface only on the first call that hits them. Opt into fail-fast validation so these known conflicts are caught during registration:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly);
    options.ValidateOnBuild = true; // throws from AddSimpleMediator on a bad configuration
});

// …or validate explicitly, anywhere after registration:
services.ValidateSimpleMediator();
```

Basic structural validation runs during **every** `AddSimpleMediator` call, with no opt-in:

- open-generic mappings that Microsoft DI cannot close, and scanned open-generic request handlers with no inferable mapping;
- concrete handler/behavior implementations that are abstract, non-public-constructor, or not activatable by DI;
- a `Singleton` custom-mapped open-generic handler with a `Scoped` or `Transient` direct constructor dependency (see [Lifetimes of custom-mapped open-generic handlers](#open-generic-request-handlers));
- `RequireScopedMediator` set to conflicting values by different `AddSimpleMediator` calls;
- a behavior registered twice for the same request/response pair with conflicting lifetimes.

`ValidateOnBuild` (or an explicit `services.ValidateSimpleMediator()`) additionally enables the *conflict* checks, which are the ones that would otherwise surface on live traffic:

- multiple registrations for the same closed `IRequestHandler<,>` (whether by type, factory, or instance);
- a closed request handler that is *also* matched by a scanned SimpleMediator open-generic handler or by a native DI open-generic `IRequestHandler<,>` registration.

Conflict validation can only see request types that have a closed registration; it cannot predict every request type an application may send. For the complete constructor dependency graph, also enable the host provider's `ValidateOnBuild` and `ValidateScopes`.

> **Recommended for production:** set `options.ValidateOnBuild = true`. Without it, a duplicate handler or
> an ambiguity surfaces as a `RequestHandlerResolutionException` on the *first request* that hits it
> — i.e. in production, under load, on one endpoint.

> **Modular registration:** `AddSimpleMediator` may be called more than once — e.g. once per module. Closed handlers accumulate, and scanned open-generic handlers are merged across calls. Scanned types are ordered by `FullName` within each assembly so composition does not depend on reflection enumeration order. Explicitly configured `NotificationPublishStrategy` and `OpenGenericResolutionCacheCapacity` override previous values; a later call that leaves them at their defaults preserves the existing module configuration. `RequireScopedMediator` follows the same rule — a call that does not set it keeps the earlier value — with one addition: two calls that set it explicitly to *different* values throw `InvalidOperationException`, so one module cannot silently disable the guard for the others. Once `ValidateOnBuild` is enabled by any module—or `ValidateSimpleMediator()` is called explicitly—subsequent modular calls keep validation enabled so newly added registrations are checked as part of the accumulated composition.

## Assembly Scanning Rules

Scanning is indiscriminate within the assemblies you register, so it is worth knowing exactly what is
considered and what is skipped.

**Registered:** closed types implementing `IRequestHandler<,>`, `INotificationHandler<>`,
`IPreRequestHandler<,>`, `IPostRequestHandler<,>`, or `IRequestExceptionHandler<,>`; open generic
implementations of the same interfaces. A type implementing several of them is registered for each.

**Skipped silently (never a startup error):**

- an open generic **nested inside a generic type** — `Outer<T>.Handler<TU>`. Nothing in the
  application can supply `Outer<T>`'s argument, so no caller can ever close it. Such a type used to
  abort the whole composition root, which meant one unreachable type could take the application down
  at startup.
- any other type that still has unbound generic parameters.
- anything excluded by a type filter.

**Rejected:** an *inferable* open-generic request handler whose mapping Microsoft DI cannot close
is registered through SimpleMediator's matcher; one that is not inferable is a configuration error.

To exclude types from discovery — generated code, obsolete handlers, a composition-root type that
happens to implement a handler interface — pass a filter:

```csharp
services.AddSimpleMediator(options =>
{
    options.RegisterAssembly(typeof(Program).Assembly, type => !type.IsDefined(typeof(ExcludeFromMediation)));
    options.RegisterAssembly(typeof(Contracts).Assembly, type => type.Namespace?.StartsWith("App.Handlers") == true);
});
```

Calling `RegisterAssembly` twice for the same assembly **narrows** the accepted set (both predicates
must pass) rather than replacing the earlier one, so modular composition cannot silently re-admit a
type another module excluded.


## Observability
SimpleMediator keeps the core limited to the DI abstractions dependency; cross-cutting concerns like logging, metrics, tracing, and correlation IDs are implemented as ordinary pipeline behaviors. A timing + tracing behavior, for example:

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

## Benchmarks

Repeatable microbenchmarks are provided in `benchmarks/SimpleMediator.Benchmarks`. Run them in Release mode with the BenchmarkDotNet harness:

```bash
dotnet run -c Release -f net10.0 --project benchmarks/SimpleMediator.Benchmarks -- --filter '*MediatorBenchmarks*'
```

The suite measures request dispatch against a direct handler call and compares sequential and parallel notification publication. Both the mediator and the baseline handler are resolved from a scope, and the baseline handler is `async`, so the comparison isolates mediator dispatch overhead instead of measuring a completed task against a state machine. `MediatorSend_WithBehaviors` measures the cost of a three-behavior chain. BenchmarkDotNet reports runtime, operating system, CPU, throughput, and memory allocation; use its generated reports when comparing changes. Run on an otherwise idle machine and compare results only across matching hardware and runtime configurations. Use `net8.0` instead of `net10.0` to benchmark that target framework. For a quick harness check (not performance comparisons), append `--job Dry`.

## AOT & Trimming
Native AOT and trimming are **explicitly out of scope** for SimpleMediator. The implementation relies on assembly scanning, runtime `MakeGenericType`, `Activator.CreateInstance`, and `ActivatorUtilities`, and the supported deployment target is classic JIT execution such as standard ASP.NET Core.

`AddSimpleMediator`, `ISender`, `IPublisher`, and the public `Mediator` dispatch methods are annotated with `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` so unsupported usage produces warnings through both DI and direct-construction entry points. Do not enable `PublishTrimmed` or `PublishAot`; a source-generated/AOT-safe dispatch mode is not part of the current support contract.

> **Consumer builds with trimming or AOT.** Those annotations mean a project with `PublishTrimmed`
> or `PublishAot` **and** `TreatWarningsAsErrors` fails to compile on `AddSimpleMediator` and on
> every `Send`/`Publish` call site, with `IL2026` and `IL3050`. If you are knowingly running
> trimming/AOT and accept that handler types are not trim-safe, suppress them per project:
>
> ```xml
> <PropertyGroup>
>   <NoWarn>$(NoWarn);IL2026;IL3050</NoWarn>
> </PropertyGroup>
> ```
>
> Suppressing the warning does not make trimming work: handlers reachable only through reflection
> can still be trimmed away. Verify with an actual trimmed publish before relying on it.

The package does not inject transitive global usings into consumer projects. Add `using SimpleMediator.Interfaces;` explicitly, or enable the namespace in the consuming project if desired.

## Why SimpleMediator?

SimpleMediator uses a **hybrid approach**:
1. **Discovery**: Reflection is used once at startup to find handlers.
2. **Wrapper creation**: The first time a request or notification type is used, SimpleMediator closes a generic wrapper type (`MakeGenericType`) and instantiates it once with `Activator.CreateInstance`; the instance is cached. Concurrent first use is coalesced so only one wrapper is created per cache key.
3. **Execution**: Subsequent calls reuse the cached wrapper, which calls your handler through ordinary typed generic code, while handlers and pipeline services are resolved through Microsoft Dependency Injection on every call so lifetimes and scopes remain correct.

### What the performance actually consists of

Per `Send`, on top of your handler's own work, SimpleMediator performs:

- one bounded-cache lookup keyed by `(requestType, responseType)` — lock-free on the hot path;
- `GetServices<IRequestHandler<,>>()` for handler selection plus one each for pre-handlers, post-handlers and behaviors: four enumerable resolutions, each materialising an array, plus a `GetService<MediatorConfiguration>()` and an open-generic plan lookup;
- one handler delegate allocation plus one closure per behavior;
- one `Order` snapshot and, only when registration order is not already correct, one sort;
- the async state machines of the behavior chain.

Resolving the mediator itself (it is transient) costs two more lookups: the configuration and, for the root guard, `IServiceScopeFactory`.

The cached wrapper removes reflection from the per-call path, but the per-request DI resolutions and delegate allocations dominate. No comparative benchmark against other mediator libraries is published; measure with the suite above rather than assuming a number.

The wrapper caches are **unbounded**: one small wrapper per request/response pair or notification type actually dispatched, which is a finite set. They live on the container's configuration singleton, never in a static, so they are released with the container — the same lifetime for which Microsoft DI itself retains every service type it has resolved. A plugin host that unloads a collectible `AssemblyLoadContext` must therefore dispose the container that dispatched that context's types, as it already must for Microsoft DI. (Before 4.0 these caches were bounded at 1024 entries with FIFO eviction, which made dispatch cost jump for applications with more distinct request types than that.) The open-generic resolution-plan cache remains bounded by `OpenGenericResolutionCacheCapacity`.

## Known limitations

- No Native AOT or trimming support (see above).
- No `IStreamRequest` / `IAsyncEnumerable` request support.
- No `IPipelineContext` equivalent, so there is no way to pass per-request services or arguments alongside a request; everything flows through the ambient `IServiceProvider` of the mediator's scope.
- No handler decoration: one handler per request and no built-in decorator chain. Use `IPipelineBehavior<,>` for cross-cutting concerns.
- Handler and notification matching is **exact**, not contravariant. A handler registered for a base request or notification does not receive derived ones, even though the interfaces are declared contravariant.
- No per-request transaction or outbox; see [Partial commits](#exactly-what-reaches-an-exception-handler).
- Only Microsoft.Extensions.DependencyInjection is supported (see [Supported DI container](#supported-di-container)). The root-mediator guard is advisory and does not fire on other containers.
- Notifications have no pipeline: behaviors, pre/post handlers and exception handlers apply to requests only.

## License
This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Contributing
Contributions, pull requests, and corrections are welcome. Please open issues or submit PRs to propose improvements.
