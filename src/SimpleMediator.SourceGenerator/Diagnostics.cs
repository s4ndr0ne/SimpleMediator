using Microsoft.CodeAnalysis;

namespace SimpleMediator.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "SimpleMediator.Generator";

    public static readonly DiagnosticDescriptor GeneratorFailure = new(
        "SMG000",
        "SimpleMediator source generator failed",
        "The SimpleMediator source generator failed and emitted no registrations: {0}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateRequestHandler = new(
        "SMG001",
        "Multiple request handlers for the same request",
        "Request '{0}' with response '{1}' has more than one handler ({2}); a request can only have one handler unless all but one are excluded by an assembly filter",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InaccessibleType = new(
        "SMG002",
        "Type is not accessible from generated code",
        "'{0}' is skipped by the SimpleMediator source generator because generated code cannot reference it ({1}); make it internal or public, or register it manually",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OpenGenericMessage = new(
        "SMG003",
        "Message type is not known at compile time",
        "'{0}' contains type parameters, so the source generator cannot pre-generate its dispatch; the call fails at runtime under AddSimpleMediatorGenerated unless every closed form is sent elsewhere with a concrete type",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OpenGenericUnused = new(
        "SMG004",
        "Open-generic type matched no message",
        "Open-generic '{0}' matched no request or notification known at compile time, so no closed registration was generated for it",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BehaviorNotTypeOf = new(
        "SMG005",
        "Behavior type cannot be resolved at compile time",
        "AddBehavior argument is not a typeof(...) expression; the behavior is only pre-generated when it is declared in a scanned assembly, otherwise AddSimpleMediatorGenerated throws at startup",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AssemblyNotResolvable = new(
        "SMG006",
        "Assembly cannot be resolved at compile time",
        "RegisterAssembly argument must be 'typeof(T).Assembly', 'typeof(T).GetTypeInfo().Assembly' or 'Assembly.GetExecutingAssembly()' for the source generator to scan it; any other assembly makes AddSimpleMediatorGenerated throw at startup",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
