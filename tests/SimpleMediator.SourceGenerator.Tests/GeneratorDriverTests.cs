using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.SourceGenerator.Tests;

/// <summary>
/// Runs the generator over in-memory compilations to cover diagnostics and language-version
/// compatibility of the generated code.
/// </summary>
public sealed class GeneratorDriverTests
{
    private const string Usings = """
        using System;
        using System.Reflection;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using SimpleMediator;
        using SimpleMediator.Interfaces;

        """;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(IMediator).Assembly.Location)
            .Append(typeof(IServiceCollection).Assembly.Location)
            .Distinct(StringComparer.Ordinal);
        return [.. paths.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });

    private static GeneratorResult Run(string source, LanguageVersion languageVersion = LanguageVersion.Latest)
    {
        var parseOptions = new CSharpParseOptions(languageVersion);
        var compilation = CSharpCompilation.Create(
            "GeneratorInput",
            [CSharpSyntaxTree.ParseText(Usings + source, parseOptions)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new SimpleMediatorGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var registrations = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .FirstOrDefault(generated => generated.HintName == SimpleMediatorGenerator.RegistrationsHintName)
            .SourceText?.ToString() ?? string.Empty;

        return new GeneratorResult(
            diagnostics,
            [.. output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)],
            registrations);
    }

    private sealed record GeneratorResult(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationErrors,
        string Registrations)
    {
        public IEnumerable<string> Ids => GeneratorDiagnostics.Select(diagnostic => diagnostic.Id);
    }

    private const string Composition = """
        public static class Composition
        {
            public static void Compose(IServiceCollection services)
            {
                services.AddSimpleMediatorGenerated(o => o.RegisterAssembly(typeof(Composition).Assembly));
            }
        }

        """;

    [Fact]
    public void Generated_code_compiles_as_CSharp_7_3()
    {
        // Every construct below is C# 7.3; so must be everything the generator adds.
        var result = Run(Composition + """
            public sealed class Ping : IRequest<int> { }
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) { return Task.FromResult(1); }
            }
            public sealed class Box<T> : IRequest<T> { }
            public sealed class BoxHandler<T> : IRequestHandler<Box<T>, T>
            {
                public Task<T> Handle(Box<T> request, CancellationToken cancellationToken) { return Task.FromResult(default(T)); }
            }
            public sealed class Noise : INotification { }
            public sealed class NoiseHandler<TNotification> : INotificationHandler<TNotification> where TNotification : INotification
            {
                public Task Handle(TNotification notification, CancellationToken cancellationToken) { return Task.CompletedTask; }
            }
            public static class Usage
            {
                public static Task<int> Run(ISender sender) { return sender.Send(new Box<int>()); }
            }
            """, LanguageVersion.CSharp7_3);

        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Contains("registry.AddHandler<global::SimpleMediator.Interfaces.IRequestHandler<global::Box<int>, int>, global::BoxHandler<int>>(typeof(global::BoxHandler<>));", result.Registrations, StringComparison.Ordinal);
        Assert.Contains("registry.AddHandler<global::SimpleMediator.Interfaces.INotificationHandler<global::Noise>, global::NoiseHandler<global::Noise>>(typeof(global::NoiseHandler<>));", result.Registrations, StringComparison.Ordinal);
        Assert.Contains("registry.AddRequest<global::Ping, int>();", result.Registrations, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_request_handlers_are_reported()
    {
        var result = Run(Composition + """
            public sealed class Ping : IRequest<int> { }
            public sealed class FirstHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            public sealed class SecondHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(2);
            }
            """);

        Assert.Contains("SMG001", result.Ids);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Inaccessible_handler_is_reported_and_skipped()
    {
        var result = Run(Composition + """
            public sealed class Ping : IRequest<int> { }
            public static class Container
            {
                private sealed class HiddenHandler : IRequestHandler<Ping, int>
                {
                    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
                }
            }
            """);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal("SMG002", diagnostic.Id);
        Assert.DoesNotContain("HiddenHandler", result.Registrations, StringComparison.Ordinal);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Open_generic_message_at_a_call_site_is_reported()
    {
        var result = Run(Composition + """
            public sealed class Box<T> : IRequest<T> { }
            public static class Usage
            {
                public static Task<T> Run<T>(ISender sender) => sender.Send(new Box<T>());
            }
            """);

        Assert.Contains("SMG003", result.Ids);
    }

    [Fact]
    public void Open_generic_handler_matching_nothing_is_reported_as_info()
    {
        var result = Run(Composition + """
            public sealed class Box<T> : IRequest<T> { }
            public sealed class BoxHandler<T> : IRequestHandler<Box<T>, T>
            {
                public Task<T> Handle(Box<T> request, CancellationToken cancellationToken) => Task.FromResult(default(T)!);
            }
            """);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal("SMG004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void Non_literal_behavior_and_assembly_are_reported()
    {
        var result = Run("""
            public static class Composition
            {
                public static void Compose(IServiceCollection services, Type behavior, Assembly assembly)
                {
                    services.AddSimpleMediatorGenerated(o =>
                    {
                        o.AddBehavior(behavior);
                        o.RegisterAssembly(assembly);
                    });
                }
            }
            """);

        Assert.Equal(["SMG005", "SMG006"], result.Ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Diagnostics_are_only_reported_when_the_generated_entry_point_is_used()
    {
        var result = Run("""
            public sealed class Ping : IRequest<int> { }
            public static class Container
            {
                private sealed class HiddenHandler : IRequestHandler<Ping, int>
                {
                    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
                }
            }
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void Constraints_are_honored_when_closing_open_generics()
    {
        var result = Run(Composition + """
            public interface ICommand { }
            public sealed class Launch : IRequest<string>, ICommand { }
            public sealed class Query : IRequest<int> { }
            public sealed class CommandBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
                where TRequest : IRequest<TResponse>, ICommand
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next(cancellationToken);
            }
            public sealed class ClassOnly<TRequest, TResponse> : IPreRequestHandler<TRequest, TResponse>
                where TRequest : IRequest<TResponse>
                where TResponse : class
            {
                public Task Handle(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        Assert.Empty(result.CompilationErrors);
        Assert.Contains("global::CommandBehavior<global::Launch, string>", result.Registrations, StringComparison.Ordinal);
        Assert.DoesNotContain("global::CommandBehavior<global::Query", result.Registrations, StringComparison.Ordinal);
        Assert.Contains("global::ClassOnly<global::Launch, string>", result.Registrations, StringComparison.Ordinal);
        Assert.DoesNotContain("global::ClassOnly<global::Query", result.Registrations, StringComparison.Ordinal);
    }

    [Fact]
    public void Handlers_are_emitted_in_full_name_order()
    {
        var result = Run(Composition + """
            namespace B { public sealed class Handler : INotificationHandler<global::N> { public Task Handle(global::N n, CancellationToken c) => Task.CompletedTask; } }
            namespace A
            {
                public sealed class Outer
                {
                    public sealed class Handler : INotificationHandler<global::N> { public Task Handle(global::N n, CancellationToken c) => Task.CompletedTask; }
                }
            }
            public sealed class N : INotification { }
            """);

        var outer = result.Registrations.IndexOf("global::A.Outer.Handler>", StringComparison.Ordinal);
        var b = result.Registrations.IndexOf("global::B.Handler>", StringComparison.Ordinal);
        Assert.True(outer >= 0 && b > outer, result.Registrations);
    }

    [Fact]
    public void Referenced_assembly_in_RegisterAssembly_is_scanned()
    {
        // typeof(IMediator).Assembly is SimpleMediator itself: it declares no handlers, but must be
        // recorded as scanned so RegisterAssembly accepts it at runtime.
        var result = Run("""
            public static class Composition
            {
                public static void Compose(IServiceCollection services)
                    => services.AddSimpleMediatorGenerated(o => o.RegisterAssembly(typeof(IMediator).Assembly));
            }
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Contains("registry.AddAssembly(typeof(global::SimpleMediator.Interfaces.IMediator).Assembly);", result.Registrations, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        const string Source = Composition + """
            public sealed class Ping : IRequest<int> { }
            public sealed class PingHandler : IRequestHandler<Ping, int>
            {
                public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(1);
            }
            """;

        Assert.Equal(Run(Source).Registrations, Run(Source).Registrations);
    }
}
