using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SimpleMediator.SourceGenerator;

/// <summary>
/// Emits <c>AddSimpleMediatorGenerated</c>, a reflection-free composition root: every handler,
/// behavior and dispatch wrapper is discovered at compile time and registered as a closed type, so
/// the application can be trimmed and published with Native AOT.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SimpleMediatorGenerator : IIncrementalGenerator
{
    internal const string EntryPointHintName = "SimpleMediatorGeneratedExtensions.g.cs";
    internal const string RegistrationsHintName = "SimpleMediatorGeneratedRegistrations.g.cs";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Emitted before analysis so the entry point is part of the compilation the generator
        // inspects: calls to AddSimpleMediatorGenerated and the lambdas passed to it bind normally.
        context.RegisterPostInitializationOutput(static output =>
            output.AddSource(EntryPointHintName, SourceText.From(Emitter.EntryPoint, Encoding.UTF8)));

        var invocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && IsCandidate(invocation),
                static (syntaxContext, _) => (InvocationExpressionSyntax)syntaxContext.Node)
            .Collect();

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(invocations),
            static (output, source) => Execute(output, source.Left, source.Right));
    }

    private static bool IsCandidate(InvocationExpressionSyntax invocation)
        => GetMethodName(invocation) is
            "Send" or "Publish" or "AddBehavior" or "RegisterAssembly" or "AddSimpleMediatorGenerated";

    internal static string? GetMethodName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null,
        };

    private static void Execute(
        SourceProductionContext output,
        Compilation compilation,
        ImmutableArray<InvocationExpressionSyntax> invocations)
    {
        var knownSymbols = KnownSymbols.TryCreate(compilation);
        if (knownSymbols is null)
        {
            return;
        }

        RegistrationModel model;
#pragma warning disable CA1031 // A generator bug must surface as a diagnostic, never crash the build.
        try
        {
            model = new ModelBuilder(compilation, knownSymbols, output.CancellationToken).Build(invocations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            output.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.GeneratorFailure,
                Location.None,
                exception.GetType().Name + ": " + exception.Message));
            return;
        }
#pragma warning restore CA1031

        output.AddSource(RegistrationsHintName, SourceText.From(Emitter.EmitRegistrations(model), Encoding.UTF8));

        if (model.UsesGeneratedEntryPoint)
        {
            foreach (var diagnostic in model.Diagnostics)
            {
                output.ReportDiagnostic(diagnostic);
            }
        }
    }
}
