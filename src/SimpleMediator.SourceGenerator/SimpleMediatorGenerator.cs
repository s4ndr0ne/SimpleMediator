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
    internal const string BuildModelStepName = "BuildModel";

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

        var invocationSignatures = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation && IsCandidate(invocation),
                static (syntaxContext, cancellationToken) => GetInvocationSignature(syntaxContext, cancellationToken))
            .Collect()
            .Select(static (signatures, _) => new EquatableArray<InvocationSignature>(signatures));

        var declarationHeaders = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsRelevantDeclarationSyntax(node),
                static (syntaxContext, _) => GetDeclarationHeader(syntaxContext.Node))
            .Collect()
            .Select(static (headers, _) => new EquatableArray<string>(headers));

        var compilationMetadata = context.CompilationProvider
            .Select(static (compilation, _) => GetCompilationMetadataSignature(compilation));

        var fingerprint = declarationHeaders
            .Combine(invocationSignatures)
            .Combine(compilationMetadata)
            .Select(static (tuple, _) => new CompilationFingerprint(tuple.Left.Left, tuple.Left.Right, tuple.Right));

        var modelProvider = context.CompilationProvider
            .Combine(invocations)
            .Combine(fingerprint)
            .Select(static (tuple, _) => new BuildInput(tuple.Left.Left, tuple.Left.Right, tuple.Right))
            .WithComparer(BuildInputComparer.Instance)
            .Select(static (source, cancellationToken) => BuildModel(source.Compilation, source.Invocations, cancellationToken))
            .WithTrackingName(BuildModelStepName);

        context.RegisterSourceOutput(
            modelProvider,
            static (output, result) => EmitOutput(output, result));
    }

    private static bool IsRelevantDeclarationSyntax(SyntaxNode node)
        => node is BaseTypeDeclarationSyntax
            or UsingDirectiveSyntax
            or ExternAliasDirectiveSyntax
            or BaseNamespaceDeclarationSyntax
            or AttributeListSyntax { Target.Identifier.ValueText: "assembly" or "module" };

    private static string GetDeclarationHeader(SyntaxNode node)
    {
        switch (node)
        {
            case BaseTypeDeclarationSyntax typeDeclaration:
            {
                var sb = new StringBuilder(128);
                for (var parent = typeDeclaration.Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent is BaseTypeDeclarationSyntax parentType)
                    {
                        sb.Append(parentType.Identifier.ValueText).Append('+');
                    }
                    else if (parent is BaseNamespaceDeclarationSyntax parentNamespace)
                    {
                        sb.Append(parentNamespace.Name).Append('.');
                    }
                }

                sb.Append(typeDeclaration.AttributeLists)
                  .Append('|')
                  .Append(typeDeclaration.Modifiers)
                  .Append('|')
                  .Append(typeDeclaration.RawKind)
                  .Append('|')
                  .Append(typeDeclaration.Identifier.ValueText);

                if (typeDeclaration is TypeDeclarationSyntax structuredType)
                {
                    sb.Append('|')
                      .Append(structuredType.TypeParameterList)
                      .Append('|')
                      .Append(structuredType.ParameterList)
                      .Append('|')
                      .Append(structuredType.BaseList)
                      .Append('|')
                      .Append(structuredType.ConstraintClauses);

                    foreach (var member in structuredType.Members)
                    {
                        if (member is ConstructorDeclarationSyntax ctor)
                        {
                            sb.Append("|ctor:")
                              .Append(ctor.Modifiers)
                              .Append(':')
                              .Append(ctor.ParameterList.Parameters.Count);
                        }
                    }
                }
                else
                {
                    sb.Append('|').Append(typeDeclaration.BaseList);
                }

                return sb.ToString();
            }

            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                return "ns:" + namespaceDeclaration.Name;

            default:
                return node.ToString();
        }
    }

    private static InvocationSignature GetInvocationSignature(
        GeneratorSyntaxContext syntaxContext,
        CancellationToken cancellationToken)
    {
        var invocation = (InvocationExpressionSyntax)syntaxContext.Node;
        var symbolInfo = syntaxContext.SemanticModel.GetSymbolInfo(invocation, cancellationToken);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        var methodDisplay = symbol is IMethodSymbol method
            ? method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : string.Empty;

        var arguments = invocation.ArgumentList.Arguments;
        var argumentTypes = string.Empty;
        if (arguments.Count > 0)
        {
            var sb = new StringBuilder(64);
            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('|');
                }

                var argument = arguments[i].Expression;
                if (argument is TypeOfExpressionSyntax typeOf)
                {
                    sb.Append(syntaxContext.SemanticModel.GetTypeInfo(typeOf.Type, cancellationToken).Type?
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }
                else
                {
                    sb.Append(syntaxContext.SemanticModel.GetTypeInfo(argument, cancellationToken).Type?
                        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    var constant = syntaxContext.SemanticModel.GetConstantValue(argument, cancellationToken);
                    if (constant.HasValue)
                    {
                        sb.Append('=').Append(constant.Value ?? "null");
                    }
                }
            }

            argumentTypes = sb.ToString();
        }

        return new InvocationSignature(methodDisplay, argumentTypes, invocation.ArgumentList.ToString());
    }

    private static string GetCompilationMetadataSignature(Compilation compilation)
    {
        var sb = new StringBuilder(128);
        sb.Append(compilation.AssemblyName).Append('|').Append(compilation.Options.OutputKind);
        foreach (var reference in compilation.References)
        {
            sb.Append('|').Append(reference.Display);
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assemblySymbol)
            {
                sb.Append(':').Append(assemblySymbol.Identity);
            }
        }

        return sb.ToString();
    }

    private static bool IsCandidate(InvocationExpressionSyntax invocation)
        => GetMethodName(invocation) is
            "Send" or "SendValueTask" or "CreateStream" or "Publish" or "AddBehavior" or "AddStreamBehavior" or "AddNotificationBehavior" or "RegisterAssembly" or "AddSimpleMediatorGenerated";

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

    private readonly struct InvocationSignature(string methodDisplay, string firstArgType, string argumentListSyntax)
        : IEquatable<InvocationSignature>
    {
        private readonly string _methodDisplay = methodDisplay;
        private readonly string _firstArgType = firstArgType;
        private readonly string _argumentListSyntax = argumentListSyntax;

        public bool Equals(InvocationSignature other)
            => string.Equals(_methodDisplay, other._methodDisplay, StringComparison.Ordinal) &&
               string.Equals(_firstArgType, other._firstArgType, StringComparison.Ordinal) &&
               string.Equals(_argumentListSyntax, other._argumentListSyntax, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is InvocationSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(_methodDisplay);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_firstArgType);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_argumentListSyntax);
                return hash;
            }
        }
    }

    private readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _items = items;

        public bool Equals(EquatableArray<T> other)
        {
            if (_items.IsDefault)
            {
                return other._items.IsDefault;
            }

            if (other._items.IsDefault || _items.Length != other._items.Length)
            {
                return false;
            }

            for (var i = 0; i < _items.Length; i++)
            {
                if (!_items[i].Equals(other._items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
            => _items.IsDefault ? 0 : _items.Length;
    }

    private readonly struct CompilationFingerprint(
        EquatableArray<string> declarationHeaders,
        EquatableArray<InvocationSignature> invocationSignatures,
        string compilationMetadata) : IEquatable<CompilationFingerprint>
    {
        private readonly EquatableArray<string> _declarationHeaders = declarationHeaders;
        private readonly EquatableArray<InvocationSignature> _invocationSignatures = invocationSignatures;
        private readonly string _compilationMetadata = compilationMetadata;

        public bool Equals(CompilationFingerprint other)
            => _declarationHeaders.Equals(other._declarationHeaders) &&
               _invocationSignatures.Equals(other._invocationSignatures) &&
               string.Equals(_compilationMetadata, other._compilationMetadata, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is CompilationFingerprint other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = _declarationHeaders.GetHashCode();
                hash = (hash * 397) ^ _invocationSignatures.GetHashCode();
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_compilationMetadata);
                return hash;
            }
        }
    }

    private readonly struct BuildInput(
        Compilation compilation,
        ImmutableArray<InvocationExpressionSyntax> invocations,
        CompilationFingerprint fingerprint)
    {
        public Compilation Compilation { get; } = compilation;

        public ImmutableArray<InvocationExpressionSyntax> Invocations { get; } = invocations;

        public CompilationFingerprint Fingerprint { get; } = fingerprint;
    }

    private sealed class BuildInputComparer : IEqualityComparer<BuildInput>
    {
        public static readonly BuildInputComparer Instance = new();

        public bool Equals(BuildInput x, BuildInput y) => x.Fingerprint.Equals(y.Fingerprint);

        public int GetHashCode(BuildInput obj) => obj.Fingerprint.GetHashCode();
    }
}
