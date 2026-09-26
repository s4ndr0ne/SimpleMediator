# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [4.1.0] - 2026-09-26

### Added
- **netstandard2.0 target** alongside `net8.0` and `net10.0`, for legacy consumers (.NET Framework 4.7.2+, .NET Core 2.x). `IAsyncDisposable`/`ValueTask` flow transitively via `Microsoft.Bcl.AsyncInterfaces` on that target only; the public API surface is identical across targets, with one documented difference: `IOrderedPipelineBehavior.Order` and `IRequestExceptionHandler<,>.Order` have no default interface implementation on netstandard2.0 (not supported by the runtime), so implementers must declare the property — returning `0` reproduces the default.
- SDK pinning via `global.json` (10.0.400, `latestFeature` roll-forward), honored by CI.
- Central package versioning via `Directory.Packages.props` with transitive pinning.
- Dependabot for NuGet and GitHub Actions, weekly, with grouped updates (test stack, Microsoft.Extensions, analyzers/packaging, benchmarks).
- `NuGet.config` locked to nuget.org with package source mapping, so machine-level private feeds cannot enter resolution (dependency-confusion protection).
- Repository governance: `CODEOWNERS`, pull request template, bug/feature issue forms, `SECURITY.md` (private vulnerability reporting, supported versions) and `CONTRIBUTING.md` (layout, build/test commands, conventions, release process).
- Trim/AOT analysis for the library: `IsAotCompatible` on `net8.0`/`net10.0`. Every internal reflection path is now annotated (`[DynamicallyAccessedMembers]` for metadata-only access, `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` where generic types are closed at runtime), so trim/AOT warnings surface only at consumer call sites.
- **Native AOT support via a source generator:** new opt-in package `s4ndr0ne.SimpleMediator.SourceGenerator` (depends on the runtime package at the same version). It discovers requests, notifications, handlers, pre/post/exception handlers and behaviors at compile time and emits `AddSimpleMediatorGenerated(...)`, a drop-in replacement for `AddSimpleMediator(...)` taking the same `SimpleMediatorOptions`. Dispatch wrappers are pre-generated and open-generic handlers/behaviors are closed at compile time, so void (`Unit`) and value-type responses work under Native AOT with no trim/AOT warnings. Registration semantics match assembly scanning (`RegisterAssembly` order and filters, `AddBehavior` order, lifetimes, `ValidateOnBuild`). Diagnostics `SMG000`–`SMG006` report duplicate handlers, inaccessible types, open-generic call sites and non-constant `RegisterAssembly`/`AddBehavior` arguments. Requires Roslyn 4.8+ (.NET 8 SDK / Visual Studio 17.8).
- A `Mediator` constructed directly takes the source-generated dispatch table from the provider it is given, and `AddSimpleMediatorGenerated()` without options registers only that table, so manually registered handlers work under Native AOT. Under Native AOT, constructing `Mediator` from a provider without any SimpleMediator registration now throws `InvalidOperationException` with guidance at construction, instead of `NotSupportedException` on the first value-type request (that scenario was never supported under AOT; JIT behavior is unchanged).
- `SimpleMediator.Generated` namespace (`GeneratedMediatorRegistry`, `GeneratedMediatorRegistration`): the trim-safe runtime hooks called by generated code. Public for the generator's use only; not intended to be called directly.
- `samples/SimpleMediator.AotSample` and an `aot-smoke` CI job that publishes it with Native AOT through `AddSimpleMediatorGenerated`, fails on any trim/AOT warning, and runs the self-checking native binary (request/response, `Unit`, value types, open generics, behaviors, pre/post handlers, notifications, exception handlers) as a blocking check.
- `eng/verify-package.sh` also verifies the generator package from a clean consumer: only the generator package is referenced, the build runs the trim/AOT analyzers with warnings as errors, and generated dispatch is exercised end to end.

### Changed
- Trim/AOT annotations moved from the dispatch APIs to the composition root. `ISender.Send`, `IPublisher.Publish` and the public `Mediator` methods are no longer annotated, because dispatch is AOT-safe when the container was composed with `AddSimpleMediatorGenerated`; `AddSimpleMediator` and `ValidateSimpleMediator` keep `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. `Send`/`Publish` call sites no longer produce `IL2026`/`IL3050`. A custom `ISender`/`IPublisher`/`IMediator` implementation that annotated its own `Send`/`Publish` with `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` now gets `IL2046`/`IL3051` (annotation mismatch) in trimming-enabled builds and should drop those attributes. Warning-only; no binary or source break.
- `ValidateSimpleMediator` is now annotated with `[RequiresUnreferencedCode]` in addition to `[RequiresDynamicCode]`, reflecting that it inspects handler types via reflection. Warning-only; no API change.
- `SimpleMediatorOptions.AddBehavior(Type)` declares `[DynamicallyAccessedMembers(Interfaces | PublicConstructors)]` on its parameter. `typeof(...)` arguments are unaffected; trimming-enabled callers passing a non-constant `Type` get an `IL2067` warning. Warning-only; no API change.
- Repository layout: library-only `src/`; tests moved to `tests/` (`SimpleMediator.Tests`, `SimpleMediator.IntegrationTests`, `SimpleMediator.SafetyTests`), the console demo to `samples/SimpleMediator.Console`, and the benchmarks project added to the solution. Solution folders now mirror the directory layout.
- Internal namespaces now match their folders (`SimpleMediator.Configuration`, `SimpleMediator.Infrastructure`); the public API namespaces are unchanged, and the three public configuration types moved to the project root because their namespace is `SimpleMediator`.

## [4.0.0] - 2026-09-25

### Changed
- **BREAKING (binary):** `Send` and `Publish` moved from `IMediator` to `ISender` / `IPublisher`. Source code that calls or implements `IMediator` compiles unchanged, but assemblies compiled against an earlier version must be recompiled (a call through `IMediator` now binds to the base interface member). Mocks that set up `IMediator.Send` still work; code that depends on `ISender` must be given a mock of `ISender`.
- `IOrderedPipelineBehavior.Order` is now a default interface member defaulting to `0`, matching `IRequestExceptionHandler<,>.Order`. Both ordering contracts now behave identically, and a behavior may omit `Order`.
- Native-compatible open-generic request handlers (`Handler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>`) are registered as ordinary open-generic DI services and follow `DefaultLifetime` with container-managed disposal. Only custom-mapped handlers (e.g. `EchoHandler<T> : IRequestHandler<EchoRequest<T>, T>`) use SimpleMediator's own activation, and they now follow `DefaultLifetime` as documented instead of always being transient. Handler decoration remains unsupported; use `IPipelineBehavior<,>` for cross-cutting concerns.
- The README documents actual behaviour: the scope contract, the real open-generic lifetime matrix, exactly which failures reach an exception handler, assembly-scanning rules, the true per-`Send` cost, AOT/trimming build impact for consumers, and a known-limitations list.
- **BREAKING:** pipeline behaviors with equal `Order` now run in registration order (FIFO): the first registered behavior is outermost and runs first, matching MediatR / ASP.NET Core middleware semantics. Previously the last registered behavior ran first (LIFO, an artifact of `OrderByDescending` + `Aggregate`). With distinct `Order` values the execution order is unchanged. If you relied on the LIFO tie-break, either assign distinct `Order` values or swap your `AddBehavior` registration order.
- `IPipelineBehavior.Order` documentation now states the FIFO tie-break contract explicitly.
- The default language version is now SDK-controlled (`LangVersion=default`) for reproducible framework-aligned compilation.
- Native AOT and trimming are explicitly out of scope; classic JIT execution is the supported deployment model.
- Documentation now explicitly distinguishes assembly-scanned handlers from pipeline behaviors, which must be registered with `AddBehavior`, and documents the required request/operation scope for long-running applications.
- Parallel notification dispatch now rethrows the original exception for a single failed handler and reserves `AggregateException` for multiple failures.
- Once `ValidateOnBuild` is enabled or validation is requested explicitly, subsequent modular `AddSimpleMediator` calls keep validating the accumulated registration set.
- Assembly scanning now uses a stable type-name order so module composition does not depend on reflection enumeration order.

### Added
- `ISender` (request/response) and `IPublisher` (notifications) interfaces. `IMediator` now derives from both, and `AddSimpleMediator` registers `ISender` and `IPublisher` as transient services that forward to the `IMediator` registration.
- README section documenting that only Microsoft.Extensions.DependencyInjection is a supported container.
- `SimpleMediatorOptions.RequireScopedMediator` (default `true`): resolving `IMediator` from the root service provider now throws `MediatorScopeException` instead of silently promoting every scoped dependency to a process-wide singleton.
- `IServiceScopeFactory.CreateMediatorScope()` / `IServiceProvider.CreateMediatorScope()`: creates a scope and the mediator resolved from it as one disposable handle, for background services, hosted services, and queue consumers.
- `SimpleMediatorOptions.RegisterAssembly(Assembly, Func<Type, bool>)`: excludes types from discovery. Registering the same assembly again narrows the accepted set rather than replacing the predicate.
- `RequestHandlerResolutionException` (derives from `InvalidOperationException`) for "no handler" / "multiple handlers". It is deliberately not offered to `IRequestExceptionHandler<,>`, because handler selection is a wiring defect rather than a request failure. Existing `catch (InvalidOperationException)` sites are unaffected.
- `SimpleMediator.SafetyTest` project pinning the scope, lifetime, ordering, and failure-routing contracts.
- .NET analyzers (all rules, warnings as errors) and public API analyzers to enforce API surface stability.
- Public AOT/trimming annotations on every mediator dispatch entry point, so unsupported runtime-code generation is reported whether the mediator is registered through DI or constructed directly.
- Single-flight creation for cached wrappers and open-generic resolution plans, preventing duplicate factory compilation under concurrent first use.
- NuGet metadata improvements, including SourceLink, copyright, release notes, and a non-transitive build props file.
- Clean package-consumer smoke testing and Generic Host integration tests covering scopes, concurrent mediation, async disposal, and sequential notifications.

### Fixed
- **CI:** the package smoke test (`eng/verify-package.sh`) resolved `IMediator` from the root provider and therefore failed with `MediatorScopeException`. It now resolves the mediator from a scope, uses `ISender`, and additionally asserts that root resolution is rejected.
- **Performance:** the request and notification wrapper caches are no longer bounded at 1024 entries with FIFO eviction. Applications dispatching more distinct request types than that kept evicting hot wrappers and rebuilding them on the next call. The caches are now unbounded (one entry per dispatched type), single-flight, and recover from a faulted creation. Wrappers are created with `Activator.CreateInstance` instead of a one-shot `Expression.Compile`.
- **Correctness:** singleton custom-mapped handler validation now reads lifetimes as Microsoft DI resolves them: the **last** non-keyed registration of a dependency (it used to read the first), every registration for an `IEnumerable<T>` dependency, and the open-generic registration for a dependency closed over the handler's type parameter such as `ILogger<Handler<T>>` (previously always rejected as unknowable). The `[ActivatorUtilitiesConstructor]` constructor is honoured; otherwise every public constructor is checked.
- **Correctness:** `RequireScopedMediator` across modular `AddSimpleMediator` calls was combined with a logical AND, so any module disabling it disabled it everywhere (the README claimed the opposite). A call that does not set it now keeps the earlier value, and conflicting explicit values throw `InvalidOperationException`.
- **Diagnostics:** a pipeline behavior that returns a `null` `Task` is now reported by name. The error used to name the innermost behavior regardless of which one returned `null`.
- **Correctness:** the root-mediator guard now also rejects `new Mediator(rootProvider)` constructed by hand; it previously only caught resolution through DI.
- **Correctness:** a `Singleton` custom-mapped open-generic request handler no longer captures the first request scope's provider. It is now built from the root provider, and a singleton custom handler that declares a `Scoped` or `Transient` constructor dependency is rejected during registration. Previously such a handler held — and handed out — a dependency from an already-disposed scope.
- **Correctness:** failures raised while *constructing* the request handler, a behavior, or a pre/post handler are now routed to `IRequestExceptionHandler<,>`. They used to escape the exception pipeline entirely, so a global catch-all silently missed missing-dependency and bad-configuration failures.
- **Correctness:** if the `IRequestExceptionHandler<,>` instances cannot themselves be resolved, the original request exception is rethrown instead of being replaced by the unrelated DI error.
- **Correctness:** an open generic handler nested inside a generic type (`Outer<T>.Handler<TU>`) is now skipped during scanning instead of aborting the whole composition root. Such a type can never be closed by any caller, so it no longer takes the application down at startup.
- **Correctness:** a handler, pre/post handler, or behavior that returns `null` instead of a `Task` now fails with an `InvalidOperationException` naming the handler instead of a bare `NullReferenceException`.
- **Correctness:** pipeline behavior and exception handler `Order` is read once per request and sorted from that snapshot, so an `Order` that changes between reads can no longer produce an inconsistent sort.
- **Startup:** registering the same pipeline behavior for the same request/response pair with conflicting lifetimes is reported instead of being silently resolved by `TryAddEnumerable` discard order.
- Removed the package build props file so installing SimpleMediator does not add a transitive global using to consumer projects.
- Faulted cache factory entries are evicted, allowing a later resolution attempt to retry after a transient failure.
- Reworked bounded cache eviction bookkeeping so failed factory entries cannot accumulate stale keys or evict live replacements.
- Open-generic handler mappings and concrete handler/behavior implementations now fail structural validation during registration when they cannot be activated by Microsoft DI, including scanned open-generic request handlers without a public constructor.

## [3.1.0] - 2026-07-21

### Fixed
- Cancellation handling: `OperationCanceledException` is treated as control flow, never offered to `IRequestExceptionHandler<,>`, and propagates straight to the caller.
- `Publish` in `Parallel` mode now surfaces `OperationCanceledException` itself (instead of an `AggregateException`) when every faulted handler throws one and the supplied token is cancelled.

### Added
- Open-generic request handler support with `OpenGenericMatcher` (type-argument inference covering nested generics and single-dimension arrays), per-request lifetime, and cached resolution plans.
- `MediatorConfiguration` runtime settings and `ValidateOnBuild` / `services.ValidateSimpleMediator()` fail-fast startup validation.
- `IRequestExceptionHandler<TRequest, TResponse>` with `Order` property and catch-all open-generic support.
- `NotificationPublishStrategy` (`Sequential` default / `Parallel`) for configurable notification dispatch.

## [3.0.0] - 2026-06-21

### Changed
- Updated .NET version compatibility targets (`net8.0` / `net10.0`).

### Fixed
- Error handling in the mediator pipeline.

## [2.1.0]

### Fixed
- Version metadata.

## [2.0.0]

### Added
- `IPipelineBehavior<TRequest, TResponse>` execution ordered by `Order` property (lower runs first / outermost), independent of registration order.

### Changed
- Refactored `Mediator` to use cached compiled expression trees for handler factory creation, improving performance and reducing memory usage.
- Request and notification handling now share the same `IServiceProvider` for correct scoped service resolution.

### Fixed
- Pipeline behavior registration, assembly scanning and handler cache bugs.

## [1.1.1]

### Fixed
- README corrections.

## [1.1.0]

### Added
- Pre/post request handler support (`IPreRequestHandler<,>` / `IPostRequestHandler<,>`).
- Enhanced cancellation token support in request handling.

## [1.0.1]

### Changed
- Upgraded target to .NET 10.
- Added symbol package (`snupkg`) publishing.
- Updated workflow permissions.

## [1.0.0] - Initial release

- Lightweight mediator pattern implementation built on `Microsoft.Extensions.DependencyInjection`.
