# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- .NET analyzers (all rules, warnings as errors) and public API analyzers to enforce API surface stability.

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
