# Contributing

## Prerequisites

The SDK is pinned by `global.json` (10.0.x, `latestFeature` roll-forward). Install any
10.0.4xx SDK; `dotnet` picks it up automatically. To also run the `net8.0` test target,
install the .NET 8 runtime.

## Layout

```
src/SimpleMediator/                 the runtime library (package s4ndr0ne.SimpleMediator)
src/SimpleMediator.SourceGenerator/ Roslyn source generator for AOT (package s4ndr0ne.SimpleMediator.SourceGenerator)
tests/SimpleMediator.*Tests/        unit, safety, integration and generator tests (xunit)
samples/SimpleMediator.Console/     console demo
samples/SimpleMediator.AotSample/   Native AOT smoke test (Console sources + AddSimpleMediatorGenerated; CI aot-smoke)
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
- The source generator targets netstandard2.0 and compiles against Roslyn 4.8
  (`VersionOverride` in its csproj): do not raise that without documenting the new minimum
  SDK/Visual Studio. Generated code must stay C# 7.3 compatible. New diagnostics go in
  `AnalyzerReleases.Unshipped.md`. Registration semantics must match assembly scanning;
  `SimpleMediator.SourceGenerator.Tests` compares both modes on the same fixtures.
- Behavior contracts (scope guard, FIFO behavior ordering, exception routing, lifetime
  rules) are pinned by `SimpleMediator.SafetyTests`. A change that alters them is
  breaking, even if the API surface is unchanged.

## Pull requests

Every PR runs the full CI matrix (Ubuntu, Windows, macOS; build, tests, vulnerability
check, package smoke test). Update `CHANGELOG.md` under `[Unreleased]`. Breaking changes
are accepted only on a major release and must be marked BREAKING.

## Releasing

The next release after the published `4.0.0` is **`4.1.0`** (minor): the pending changes add
Native AOT support, a source-generator package and additive runtime APIs, without an intentional
breaking change. Keep these notes under `[Unreleased]` in `CHANGELOG.md` until the release is
published. On release day, move them under `## [4.1.0] - YYYY-MM-DD`, leave a fresh `[Unreleased]`
section above, then create and push the `v4.1.0` tag.

The release workflow validates the semver tag, builds, tests, packs, smoke-tests and creates the
GitHub release with both packages attached (runtime nupkg/snupkg and generator nupkg). Both
packages always ship with the same version. The workflow does **not** publish packages to NuGet;
publishing them there remains a separate maintainer action. The project follows
[Semantic Versioning](https://semver.org).
