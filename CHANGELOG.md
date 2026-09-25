# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [4.0.0]

### Changed
- **BREAKING:** pipeline behaviors with equal `Order` now run in registration order (FIFO): the first registered behavior is outermost and runs first, matching MediatR / ASP.NET Core middleware semantics. Previously the last registered behavior ran first (LIFO, an artifact of `OrderByDescending` + `Aggregate`). With distinct `Order` values the execution order is unchanged. If you relied on the LIFO tie-break, either assign distinct `Order` values or swap your `AddBehavior` registration order.
- `IPipelineBehavior.Order` documentation now states the FIFO tie-break contract explicitly.
- Open-generic request handlers remain intentionally transient and are created per request, regardless of `DefaultLifetime`. The resolution plan is cached, but handler instances are not. Closed handlers and all native DI registrations continue to honor `DefaultLifetime`.
- The default language version is now SDK-controlled (`LangVersion=default`) for reproducible framework-aligned compilation.
- Native AOT and trimming are explicitly out of scope; classic JIT execution is the supported deployment model.
- Documentation now explicitly distinguishes assembly-scanned handlers from pipeline behaviors, which must be registered with `AddBehavior`, and documents the required request/operation scope for long-running applications.

### Added
- .NET analyzers (all rules, warnings as errors) and public API analyzers to enforce API surface stability.
- Public AOT/trimming annotations on every mediator dispatch entry point, so unsupported runtime-code generation is reported whether the mediator is registered through DI or constructed directly.
- Single-flight creation for cached wrappers and open-generic resolution plans, preventing duplicate factory compilation under concurrent first use.
- NuGet metadata improvements, including SourceLink, copyright, release notes, and a non-transitive build props file.
- Clean package-consumer smoke testing and Generic Host integration tests covering scopes, concurrent mediation, async disposal, and sequential notifications.

### Fixed
- Removed the package build props file so installing SimpleMediator does not add a transitive global using to consumer projects.
- Faulted cache factory entries are evicted, allowing a later resolution attempt to retry after a transient failure.
- Reworked bounded cache eviction bookkeeping so failed factory entries cannot accumulate stale keys or evict live replacements.

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
