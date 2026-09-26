using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// The cached plan for resolving custom-mapped open-generic request handlers for one
/// request/response pair: the factories that build each matching handler. Holds no handler
/// instances. Native-DI-compatible open generic handlers are resolved directly through DI.
/// </summary>
internal sealed class OpenGenericResolution
{
    public static readonly OpenGenericResolution Empty = new(Array.Empty<OpenGenericHandlerFactory>());

    public IReadOnlyList<OpenGenericHandlerFactory> Factories { get; }

    public OpenGenericResolution(IReadOnlyList<OpenGenericHandlerFactory> factories) => Factories = factories;
}

internal sealed record OpenGenericHandlerFactory(
    ObjectFactory Factory,
    ServiceLifetime Lifetime,
    Type RequestType,
    Type ResponseType);
