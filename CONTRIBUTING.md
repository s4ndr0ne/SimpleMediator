# Contributing

## Prerequisites

The SDK is pinned by `global.json` (10.0.x, `latestFeature` roll-forward). Install any
10.0.4xx SDK; `dotnet` picks it up automatically. To also run the `net8.0` test target,
install the .NET 8 runtime.

## Layout

```
src/SimpleMediator/            the library (the only packable project)
tests/SimpleMediator.*Tests/   unit, safety and integration tests (xunit)
samples/SimpleMediator.Console/ console demo
samples/SimpleMediator.AotSample/ Native AOT smoke test (same sources, PublishAot; CI aot-smoke)
benchmarks/                    BenchmarkDotNet suite
eng/verify-package.sh          package consumer smoke test (used by CI)
```

Package versions live only in `Directory.Packages.props`; the SDK version only in
`global.json`; feeds only in `NuGet.config` (nuget.org, with source mapping).

## Build and test

```bash
dotnet restore
dotnet build -c Release        # warnings are errors on the library
dotnet test -c Release         # runs on net8.0 and net10.0
```

## Conventions

- Namespaces match folders; the public API namespaces are `SimpleMediator`,
  `SimpleMediator.Interfaces` and `SimpleMediator.Core` and are frozen by
  `PublicAPI.Shipped.txt`. New public surface must appear in `PublicAPI.Unshipped.txt`
  deliberately — the build fails otherwise.
- Only Microsoft.Extensions.DependencyInjection is a supported container. Do not add
  abstractions for third-party containers.
- netstandard2.0 is a supported target: no default interface members, no framework
  `ThrowIf*` guards (use the internal `ThrowHelper`), no `DistinctBy`. If an API is
  missing there, prefer a single conditional block over divergent code paths.
- Behavior contracts (scope guard, FIFO behavior ordering, exception routing, lifetime
  rules) are pinned by `SimpleMediator.SafetyTests`. A change that alters them is
  breaking, even if the API surface is unchanged.

## Pull requests

Every PR runs the full CI matrix (Ubuntu, Windows, macOS; build, tests, vulnerability
check, package smoke test). Update `CHANGELOG.md` under `[Unreleased]`. Breaking changes
are accepted only on a major release and must be marked BREAKING.

## Releasing

Releases are maintainer-only: tag `vX.Y.Z` and push; the release workflow validates the
semver tag, builds, tests, packs, smoke-tests the package and creates the GitHub release
with the nupkg/snupkg attached. The project follows [Semantic Versioning](https://semver.org).
