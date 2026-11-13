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

runtime_packages=("$package_directory"/s4ndr0ne.SimpleMediator.[0-9]*.nupkg)
generator_packages=("$package_directory"/s4ndr0ne.SimpleMediator.SourceGenerator.[0-9]*.nupkg)
outbox_packages=("$package_directory"/s4ndr0ne.SimpleMediator.Outbox.[0-9]*.nupkg)
sqlserver_outbox_packages=("$package_directory"/s4ndr0ne.SimpleMediator.Outbox.SqlServer.[0-9]*.nupkg)
if [[ "${#runtime_packages[@]}" -eq 0 ]]; then
  echo "No s4ndr0ne.SimpleMediator package found in '$package_directory'." >&2
  exit 1
fi
if [[ "${#outbox_packages[@]}" -eq 0 ]]; then
  echo "No s4ndr0ne.SimpleMediator.Outbox package found in '$package_directory'." >&2
  exit 1
fi
if [[ "${#sqlserver_outbox_packages[@]}" -eq 0 ]]; then
  echo "No s4ndr0ne.SimpleMediator.Outbox.SqlServer package found in '$package_directory'." >&2
  exit 1
fi

if [[ -z "$package_version" ]]; then
  package_version="$(unzip -p "${runtime_packages[0]}" '*.nuspec' | awk -F'[<>]' '/<version>/{print $3; exit}')"
fi

if [[ -z "$package_version" ]]; then
  echo "Unable to determine the package version." >&2
  exit 1
fi

consumer_directory="$(mktemp -d "${TMPDIR:-/tmp}/simplemediator-consumer.XXXXXX")"
outbox_consumer_directory="$(mktemp -d "${TMPDIR:-/tmp}/simplemediator-outbox-consumer.XXXXXX")"
sqlserver_consumer_directory="$(mktemp -d "${TMPDIR:-/tmp}/simplemediator-sqlserver-consumer.XXXXXX")"
generated_consumer_directory="$(mktemp -d "${TMPDIR:-/tmp}/simplemediator-generated-consumer.XXXXXX")"
trap 'rm -rf "$consumer_directory" "$outbox_consumer_directory" "$sqlserver_consumer_directory" "$generated_consumer_directory"' EXIT
export NUGET_PACKAGES="$outbox_consumer_directory/.nuget"

echo "Verifying s4ndr0ne.SimpleMediator $package_version (reflection registration)..."

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

echo "Verifying s4ndr0ne.SimpleMediator.Outbox $package_version (enqueue registration)..."
outbox_project_file="$outbox_consumer_directory/OutboxConsumer.csproj"
dotnet new console --name OutboxConsumer --framework net10.0 --output "$outbox_consumer_directory" --no-restore >/dev/null
dotnet add "$outbox_project_file" package Microsoft.Extensions.DependencyInjection --version 8.0.1 --no-restore >/dev/null
dotnet add "$outbox_project_file" package s4ndr0ne.SimpleMediator.Outbox --version "$package_version" --source "$package_directory" --no-restore >/dev/null

cat > "$outbox_consumer_directory/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;

var store = new SmokeStore();
var services = new ServiceCollection();
services.AddSingleton<IOutboxStore>(store);
services.AddSingleton<IOutboxSerializer, SmokeSerializer>();
services.AddSimpleMediatorOutbox();
using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
await scope.ServiceProvider.GetRequiredService<IOutboxPublisher>().EnqueueAsync(new SmokeNotification("package"));

if (store.Enqueued is null || store.Enqueued.Type != "smoke")
{
    throw new InvalidOperationException("The outbox package did not persist the smoke notification.");
}

Console.WriteLine("outbox enqueued");

public sealed record SmokeNotification(string Value) : INotification;

public sealed class SmokeSerializer : IOutboxSerializer
{
    public OutboxMessage Serialize(INotification notification, Guid messageId, DateTimeOffset occurredAtUtc)
        => new(messageId, "smoke", ((SmokeNotification)notification).Value, occurredAtUtc);

    public IOutboxDispatchable Deserialize(OutboxMessage message)
        => new OutboxDispatchable<SmokeNotification>(new SmokeNotification(message.Payload));
}

public sealed class SmokeStore : IOutboxStore
{
    public OutboxMessage? Enqueued { get; private set; }

    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        Enqueued = message;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxLease>> ClaimBatchAsync(
        int batchSize,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxLease>>(Array.Empty<OutboxLease>());

    public Task<bool> RenewLeaseAsync(
        OutboxLease lease,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> MarkProcessedAsync(
        OutboxLease lease,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> MarkFailedAsync(
        OutboxLease lease,
        DateTimeOffset retryAtUtc,
        string error,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
EOF

dotnet restore "$outbox_project_file" --source "$package_directory" --source https://api.nuget.org/v3/index.json >/dev/null
dotnet run --project "$outbox_project_file" --no-restore --framework net10.0

echo "Verifying s4ndr0ne.SimpleMediator.Outbox.SqlServer $package_version (DI & JsonOutboxSerializer)..."
sqlserver_project_file="$sqlserver_consumer_directory/SqlServerOutboxConsumer.csproj"
dotnet new console --name SqlServerOutboxConsumer --framework net10.0 --output "$sqlserver_consumer_directory" --no-restore >/dev/null
dotnet add "$sqlserver_project_file" package Microsoft.Extensions.DependencyInjection --version 8.0.1 --no-restore >/dev/null
dotnet add "$sqlserver_project_file" package s4ndr0ne.SimpleMediator.Outbox.SqlServer --version "$package_version" --source "$package_directory" --no-restore >/dev/null

cat > "$sqlserver_consumer_directory/Program.cs" <<'EOF'
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;
using SimpleMediator.Outbox;
using SimpleMediator.Outbox.SqlServer;

var services = new ServiceCollection();
services.AddJsonOutboxSerializer(b => b.AddNotification("smoke.v1", SmokeJsonContext.Default.SqlSmokeNotification));
services.AddSqlServerOutboxStore(o => o.ConnectionString = "Server=localhost;Database=OutboxSmoke;TrustServerCertificate=True;");
services.AddSimpleMediatorOutbox();

using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
var session = scope.ServiceProvider.GetRequiredService<ISqlServerOutboxSession>();
var serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
if (store is not SqlServerOutboxStore || session is null || serializer is not JsonOutboxSerializer)
{
    throw new InvalidOperationException("Unexpected SQL Server outbox DI resolution.");
}

var msg = serializer.Serialize(new SqlSmokeNotification("ok"), Guid.NewGuid(), DateTimeOffset.UtcNow);
if (msg.Type != "smoke.v1")
{
    throw new InvalidOperationException("Unexpected serialized message type.");
}

Console.WriteLine("sqlserver outbox verified");

public sealed record SqlSmokeNotification(string Value) : INotification;

[JsonSerializable(typeof(SqlSmokeNotification))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
EOF

dotnet restore "$sqlserver_project_file" --source "$package_directory" --source https://api.nuget.org/v3/index.json >/dev/null
dotnet run --project "$sqlserver_project_file" --no-restore --framework net10.0

if [[ "${#generator_packages[@]}" -eq 0 ]]; then
  echo "No s4ndr0ne.SimpleMediator.SourceGenerator package found; skipping the generated-registration consumer."
  exit 0
fi

# Second consumer: only the generator package is referenced, so this also proves it brings the
# runtime package as a dependency. The trim and AOT analyzers run with warnings as errors: the
# generated composition root and dispatch must not produce a single IL2026/IL3050 warning.
echo "Verifying s4ndr0ne.SimpleMediator.SourceGenerator $package_version (generated registration)..."
generated_project_file="$generated_consumer_directory/GeneratedConsumer.csproj"
dotnet new console --name GeneratedConsumer --framework net10.0 --output "$generated_consumer_directory" --no-restore >/dev/null
dotnet add "$generated_project_file" package Microsoft.Extensions.DependencyInjection --version 8.0.1 --no-restore >/dev/null
dotnet add "$generated_project_file" package s4ndr0ne.SimpleMediator.SourceGenerator \
  --version "$package_version" \
  --source "$package_directory" \
  --no-restore >/dev/null

cat > "$generated_consumer_directory/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator;
using SimpleMediator.Interfaces;

var services = new ServiceCollection();
services.AddSimpleMediatorGenerated(options =>
{
    options.RegisterAssembly(typeof(SmokeRequest).Assembly);
    options.AddBehavior(typeof(TagBehavior<,>));
});

using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
await using var mediatorScope = provider.CreateMediatorScope();
var mediator = mediatorScope.ServiceProvider.GetRequiredService<IMediator>();

var text = await mediator.Send(new SmokeRequest("package"));
var number = await mediator.Send(new Echo<int>(41));
await mediator.Send(new Ping());

if (text != "[handled:package]" || number != 42)
{
    throw new InvalidOperationException($"Unexpected generated-registration responses: '{text}', {number}.");
}

Console.WriteLine($"{text} {number}");

public sealed record SmokeRequest(string Value) : IRequest<string>;

public sealed class SmokeHandler : IRequestHandler<SmokeRequest, string>
{
    public Task<string> Handle(SmokeRequest request, CancellationToken cancellationToken)
        => Task.FromResult($"handled:{request.Value}");
}

public sealed record Echo<T>(T Value) : IRequest<T>;

public sealed class EchoHandler : IRequestHandler<Echo<int>, int>
{
    public Task<int> Handle(Echo<int> request, CancellationToken cancellationToken) => Task.FromResult(request.Value + 1);
}

public sealed record Ping : IRequest;

public sealed class PingHandler : IRequestHandler<Ping, Unit>
{
    public Task<Unit> Handle(Ping request, CancellationToken cancellationToken) => Unit.Task;
}

public sealed class TagBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken);
        return response is string text ? (TResponse)(object)$"[{text}]" : response;
    }
}
EOF

dotnet restore "$generated_project_file" \
  --source "$package_directory" \
  --source https://api.nuget.org/v3/index.json >/dev/null
dotnet build "$generated_project_file" --no-restore --nologo -v quiet \
  -p:IsAotCompatible=true -p:TreatWarningsAsErrors=true
dotnet run --project "$generated_project_file" --no-build --framework net10.0
