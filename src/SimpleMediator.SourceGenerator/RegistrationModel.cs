using Microsoft.CodeAnalysis;

namespace SimpleMediator.SourceGenerator;

/// <summary>
/// Fully rendered registration table. Every string is a <c>global::</c>-qualified C# type name, and
/// every list is already deduplicated and in emission order, so generated output is deterministic.
/// </summary>
internal sealed class RegistrationModel
{
    public RegistrationModel(
        bool usesGeneratedEntryPoint,
        IReadOnlyList<string> assemblies,
        IReadOnlyList<(string Request, string Response)> requests,
        IReadOnlyList<string> notifications,
        IReadOnlyList<Registration> handlers,
        IReadOnlyList<Registration> behaviors,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        UsesGeneratedEntryPoint = usesGeneratedEntryPoint;
        Assemblies = assemblies;
        Requests = requests;
        Notifications = notifications;
        Handlers = handlers;
        Behaviors = behaviors;
        Diagnostics = diagnostics;
    }

    /// <summary>Whether the compilation calls <c>AddSimpleMediatorGenerated</c>; diagnostics are only reported then.</summary>
    public bool UsesGeneratedEntryPoint { get; }

    /// <summary>Expressions evaluating to each scanned <see cref="System.Reflection.Assembly"/>.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    public IReadOnlyList<(string Request, string Response)> Requests { get; }

    public IReadOnlyList<string> Notifications { get; }

    public IReadOnlyList<Registration> Handlers { get; }

    public IReadOnlyList<Registration> Behaviors { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>A closed service/implementation pair and the key type the runtime matches it against.</summary>
internal readonly struct Registration
{
    public Registration(string service, string implementation, string key)
    {
        Service = service;
        Implementation = implementation;
        Key = key;
    }

    public string Service { get; }

    public string Implementation { get; }

    /// <summary>
    /// For handlers, the declared type assembly filters observe; for behaviors, the type passed to
    /// <c>AddBehavior</c>. Rendered as a <c>typeof</c> operand (open generics as <c>T&lt;,&gt;</c>).
    /// </summary>
    public string Key { get; }
}
