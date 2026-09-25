#!/usr/bin/env bash
set -euo pipefail

package_directory="${1:-./artifacts}"
package_version="${2:-}"

if [[ ! -d "$package_directory" ]]; then
  echo "Package directory '$package_directory' does not exist." >&2
  exit 1
fi

shopt -s nullglob
packages=("$package_directory"/*.nupkg)
if [[ "${#packages[@]}" -eq 0 ]]; then
  echo "No .nupkg files found in '$package_directory'." >&2
  exit 1
fi

if [[ -z "$package_version" ]]; then
  package_version="$(unzip -p "${packages[0]}" '*.nuspec' | awk -F'[<>]' '/<version>/{print $3; exit}')"
fi

if [[ -z "$package_version" ]]; then
  echo "Unable to determine the package version." >&2
  exit 1
fi

consumer_directory="$(mktemp -d "${TMPDIR:-/tmp}/simplemediator-consumer.XXXXXX")"
trap 'rm -rf "$consumer_directory"' EXIT

project_file="$consumer_directory/Consumer.csproj"
dotnet new console --name Consumer --framework net10.0 --output "$consumer_directory" --no-restore >/dev/null

# The concrete DI implementation is intentionally supplied by the consumer, just as it
# would be in an ASP.NET Core application. The package itself only brings abstractions.
dotnet add "$project_file" package Microsoft.Extensions.DependencyInjection --version 8.0.1 --no-restore >/dev/null
dotnet add "$project_file" package s4ndr0ne.SimpleMediator \
  --version "$package_version" \
  --source "$package_directory" \
  --no-restore >/dev/null

cat > "$consumer_directory/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;

var services = new ServiceCollection();
services.AddSimpleMediator();
services.AddTransient<IRequestHandler<SmokeRequest, string>, SmokeHandler>();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// The mediator must be resolved from a scope: resolving it from the root throws MediatorScopeException.
await using (var mediatorScope = provider.CreateMediatorScope())
{
    var guardTriggered = false;
    try
    {
        _ = provider.GetRequiredService<IMediator>();
    }
    catch (SimpleMediator.Core.MediatorScopeException)
    {
        guardTriggered = true;
    }

    if (!guardTriggered)
    {
        throw new InvalidOperationException("Resolving IMediator from the root provider did not throw MediatorScopeException.");
    }

    var sender = mediatorScope.ServiceProvider.GetRequiredService<ISender>();
    var response = await sender.Send(new SmokeRequest("package"));

    if (!string.Equals(response, "handled:package", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Unexpected package smoke-test response: '{response}'.");
    }

    Console.WriteLine(response);
}


public sealed record SmokeRequest(string Value) : IRequest<string>;

public sealed class SmokeHandler : IRequestHandler<SmokeRequest, string>
{
    public Task<string> Handle(SmokeRequest request, CancellationToken cancellationToken)
        => Task.FromResult($"handled:{request.Value}");
}
EOF

dotnet restore "$project_file" \
  --source "$package_directory" \
  --source https://api.nuget.org/v3/index.json >/dev/null
dotnet run --project "$project_file" --no-restore --framework net10.0
