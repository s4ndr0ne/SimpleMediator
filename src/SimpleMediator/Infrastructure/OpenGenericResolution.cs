using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator;

/// <summary>
/// The cached plan for resolving custom-mapped open-generic request handlers for one
/// request/response pair: the factories that build each matching handler. Holds no handler
/// instances. Native-DI-compatible open generic handlers are resolved directly through DI.
/// </summary>
internal sealed class OpenGenericResolution
{
    public static readonly OpenGenericResolution Empty = new(Array.Empty<ObjectFactory>());

    public IReadOnlyList<ObjectFactory> Factories { get; }

    public OpenGenericResolution(IReadOnlyList<ObjectFactory> factories) => Factories = factories;
}
