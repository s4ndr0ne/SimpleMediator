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

        var modelProvider = context.CompilationProvider
            .Combine(invocations)
            .Select(static (source, cancellationToken) => BuildModel(source.Left, source.Right, cancellationToken));

        context.RegisterSourceOutput(
            modelProvider,
            static (output, result) => EmitOutput(output, result));
    }

    private static bool IsCandidate(InvocationExpressionSyntax invocation)
        => GetMethodName(invocation) is
            "Send" or "CreateStream" or "Publish" or "AddBehavior" or "AddStreamBehavior" or "AddNotificationBehavior" or "RegisterAssembly" or "AddSimpleMediatorGenerated";

    internal static string? GetMethodName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null,
        };

    private static (RegistrationModel? Model, Diagnostic? Failure) BuildModel(
        Compilation compilation,
        ImmutableArray<InvocationExpressionSyntax> invocations,
        CancellationToken cancellationToken)
    {
        var knownSymbols = KnownSymbols.TryCreate(compilation);
        if (knownSymbols is null)
        {
            return (null, null);
        }

#pragma warning disable CA1031 // A generator bug must surface as a diagnostic, never crash the build.
        try
        {
            var model = new ModelBuilder(compilation, knownSymbols, cancellationToken).Build(invocations);
            return (model, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Diagnostic.Create(
                Diagnostics.GeneratorFailure,
                Location.None,
                exception.GetType().Name + ": " + exception.Message);
            return (null, failure);
        }
#pragma warning restore CA1031
    }

    private static void EmitOutput(
        SourceProductionContext output,
        (RegistrationModel? Model, Diagnostic? Failure) result)
    {
        if (result.Failure is not null)
        {
            output.ReportDiagnostic(result.Failure);
            return;
        }

        if (result.Model is not { } model)
        {
            return;
        }

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
