using Microsoft.CodeAnalysis;

namespace SimpleMediator.SourceGenerator;

/// <summary>
/// SimpleMediator types resolved by metadata name from the compilation being generated.
/// </summary>
internal sealed class KnownSymbols
{
    private static readonly string[] MetadataNames =
    [
        "SimpleMediator.Interfaces.IRequest`1",
        "SimpleMediator.Interfaces.INotification",
        "SimpleMediator.Interfaces.IRequestHandler`2",
        "SimpleMediator.Interfaces.INotificationHandler`1",
        "SimpleMediator.Interfaces.IPreRequestHandler`2",
        "SimpleMediator.Interfaces.IPostRequestHandler`2",
        "SimpleMediator.Interfaces.IRequestExceptionHandler`2",
        "SimpleMediator.Interfaces.IPipelineBehavior`2",
        "SimpleMediator.Interfaces.ISender",
        "SimpleMediator.Interfaces.IPublisher",
        "SimpleMediator.SimpleMediatorOptions",
        "System.Reflection.Assembly",
        "System.Reflection.IntrospectionExtensions",
        "System.Reflection.TypeInfo",
    ];

    private KnownSymbols(INamedTypeSymbol[] symbols)
    {
        Request = symbols[0];
        Notification = symbols[1];
        RequestHandler = symbols[2];
        NotificationHandler = symbols[3];
        PreRequestHandler = symbols[4];
        PostRequestHandler = symbols[5];
        RequestExceptionHandler = symbols[6];
        PipelineBehavior = symbols[7];
        Sender = symbols[8];
        Publisher = symbols[9];
        Options = symbols[10];
        Assembly = symbols[11];
        IntrospectionExtensions = symbols[12];
        TypeInfo = symbols[13];
    }

    public INamedTypeSymbol Request { get; }

    public INamedTypeSymbol Notification { get; }

    public INamedTypeSymbol RequestHandler { get; }

    public INamedTypeSymbol NotificationHandler { get; }

    public INamedTypeSymbol PreRequestHandler { get; }

    public INamedTypeSymbol PostRequestHandler { get; }

    public INamedTypeSymbol RequestExceptionHandler { get; }

    public INamedTypeSymbol PipelineBehavior { get; }

    public INamedTypeSymbol Sender { get; }

    public INamedTypeSymbol Publisher { get; }

    public INamedTypeSymbol Options { get; }

    public INamedTypeSymbol Assembly { get; }

    public INamedTypeSymbol IntrospectionExtensions { get; }

    public INamedTypeSymbol TypeInfo { get; }

    /// <summary>
    /// Returns <c>null</c> when the compilation does not reference a SimpleMediator version that
    /// ships the generated-registration hooks, in which case nothing is generated.
    /// </summary>
    public static KnownSymbols? TryCreate(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("SimpleMediator.Generated.GeneratedMediatorRegistration") is null)
        {
            return null;
        }

        var symbols = new INamedTypeSymbol[MetadataNames.Length];
        for (var i = 0; i < MetadataNames.Length; i++)
        {
            var symbol = compilation.GetTypeByMetadataName(MetadataNames[i]);
            if (symbol is null)
            {
                return null;
            }

            symbols[i] = symbol;
        }

        return new KnownSymbols(symbols);
    }

    /// <summary>
    /// Whether <paramref name="definition"/> is one of the handler interfaces discovered by assembly
    /// scanning (behaviors are opt-in and handled separately).
    /// </summary>
    public bool IsHandlerInterface(INamedTypeSymbol definition)
        => Equal(definition, RequestHandler) ||
           Equal(definition, NotificationHandler) ||
           Equal(definition, PreRequestHandler) ||
           Equal(definition, PostRequestHandler) ||
           Equal(definition, RequestExceptionHandler);

    /// <summary>Handler interfaces whose type arguments are (request, response).</summary>
    public bool IsRequestShapedInterface(INamedTypeSymbol definition)
        => Equal(definition, RequestHandler) ||
           Equal(definition, PreRequestHandler) ||
           Equal(definition, PostRequestHandler) ||
           Equal(definition, RequestExceptionHandler) ||
           Equal(definition, PipelineBehavior);

    public static bool Equal(ISymbol? left, ISymbol? right)
        => SymbolEqualityComparer.Default.Equals(left, right);
}
