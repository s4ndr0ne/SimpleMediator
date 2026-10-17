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
        "SimpleMediator.Interfaces.IStreamRequest`1",
        "SimpleMediator.Interfaces.INotification",
        "SimpleMediator.Interfaces.IRequestHandler`2",
        "SimpleMediator.Interfaces.IStreamRequestHandler`2",
        "SimpleMediator.Interfaces.INotificationHandler`1",
        "SimpleMediator.Interfaces.IPreRequestHandler`2",
        "SimpleMediator.Interfaces.IPostRequestHandler`2",
        "SimpleMediator.Interfaces.IRequestExceptionHandler`2",
        "SimpleMediator.Interfaces.IPipelineBehavior`2",
        "SimpleMediator.Interfaces.IStreamPipelineBehavior`2",
        "SimpleMediator.Interfaces.INotificationBehavior`1",
        "SimpleMediator.Interfaces.ISender",
        "SimpleMediator.Interfaces.IPublisher",
        "SimpleMediator.SimpleMediatorOptions",
        "SimpleMediator.GenerateSimpleMediatorModuleAttribute",
        "System.Reflection.Assembly",
        "System.Reflection.IntrospectionExtensions",
        "System.Reflection.TypeInfo",
    ];

    private KnownSymbols(INamedTypeSymbol[] symbols)
    {
        Request = symbols[0];
        StreamRequest = symbols[1];
        Notification = symbols[2];
        RequestHandler = symbols[3];
        StreamRequestHandler = symbols[4];
        NotificationHandler = symbols[5];
        PreRequestHandler = symbols[6];
        PostRequestHandler = symbols[7];
        RequestExceptionHandler = symbols[8];
        PipelineBehavior = symbols[9];
        StreamPipelineBehavior = symbols[10];
        NotificationBehavior = symbols[11];
        Sender = symbols[12];
        Publisher = symbols[13];
        Options = symbols[14];
        ModuleAttribute = symbols[15];
        Assembly = symbols[16];
        IntrospectionExtensions = symbols[17];
        TypeInfo = symbols[18];
    }

    public INamedTypeSymbol Request { get; }

    public INamedTypeSymbol StreamRequest { get; }

    public INamedTypeSymbol Notification { get; }

    public INamedTypeSymbol RequestHandler { get; }

    public INamedTypeSymbol StreamRequestHandler { get; }

    public INamedTypeSymbol NotificationHandler { get; }

    public INamedTypeSymbol PreRequestHandler { get; }

    public INamedTypeSymbol PostRequestHandler { get; }

    public INamedTypeSymbol RequestExceptionHandler { get; }

    public INamedTypeSymbol PipelineBehavior { get; }

    public INamedTypeSymbol StreamPipelineBehavior { get; }

    public INamedTypeSymbol NotificationBehavior { get; }

    public INamedTypeSymbol Sender { get; }

    public INamedTypeSymbol Publisher { get; }

    public INamedTypeSymbol Options { get; }

    public INamedTypeSymbol ModuleAttribute { get; }

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
           Equal(definition, StreamRequestHandler) ||
           Equal(definition, NotificationHandler) ||
           Equal(definition, PreRequestHandler) ||
           Equal(definition, PostRequestHandler) ||
           Equal(definition, RequestExceptionHandler);

    public bool IsBehaviorInterface(INamedTypeSymbol definition)
        => Equal(definition, PipelineBehavior) ||
           Equal(definition, StreamPipelineBehavior) ||
           Equal(definition, NotificationBehavior);

    /// <summary>Handler/behavior interfaces whose type arguments are (request, response).</summary>
    public bool IsRequestShapedInterface(INamedTypeSymbol definition)
        => Equal(definition, RequestHandler) ||
           Equal(definition, PreRequestHandler) ||
           Equal(definition, PostRequestHandler) ||
           Equal(definition, RequestExceptionHandler) ||
           Equal(definition, PipelineBehavior);

    /// <summary>Handler/behavior interfaces whose type arguments are (streamRequest, response).</summary>
    public bool IsStreamRequestShapedInterface(INamedTypeSymbol definition)
        => Equal(definition, StreamRequestHandler) ||
           Equal(definition, StreamPipelineBehavior);

    public static bool Equal(ISymbol? left, ISymbol? right)
        => SymbolEqualityComparer.Default.Equals(left, right);
}
