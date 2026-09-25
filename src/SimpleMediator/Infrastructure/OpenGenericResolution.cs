using Microsoft.Extensions.DependencyInjection;

namespace SimpleMediator;

/// <summary>
/// The cached plan for resolving open-generic request handlers for one request/response pair:
/// the compiled factories that build each matching handler. Holds no handler instances.
/// </summary>
internal sealed class OpenGenericResolution
{
    public static readonly OpenGenericResolution Empty = new(Array.Empty<ObjectFactory>());

    public IReadOnlyList<ObjectFactory> Factories { get; }

    public OpenGenericResolution(IReadOnlyList<ObjectFactory> factories) => Factories = factories;
}
