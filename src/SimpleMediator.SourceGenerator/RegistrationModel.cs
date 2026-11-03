using Microsoft.CodeAnalysis;

namespace SimpleMediator.SourceGenerator;

/// <summary>
/// Fully rendered registration table. Every string is a <c>global::</c>-qualified C# type name, and
/// every list is already deduplicated and in emission order, so generated output is deterministic.
/// </summary>
internal sealed class RegistrationModel : IEquatable<RegistrationModel>
{
    public RegistrationModel(
        bool usesGeneratedEntryPoint,
        string? exportedModuleName,
        IReadOnlyList<string> chainedModuleCalls,
        IReadOnlyList<string> assemblies,
        IReadOnlyList<(string Request, string Response)> requests,
        IReadOnlyList<(string Request, string Response)> streamRequests,
        IReadOnlyList<string> notifications,
        IReadOnlyList<Registration> handlers,
        IReadOnlyList<Registration> behaviors,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        UsesGeneratedEntryPoint = usesGeneratedEntryPoint;
        ExportedModuleName = exportedModuleName;
        ChainedModuleCalls = chainedModuleCalls;
        Assemblies = assemblies;
        Requests = requests;
        StreamRequests = streamRequests;
        Notifications = notifications;
        Handlers = handlers;
        Behaviors = behaviors;
        Diagnostics = diagnostics;
    }

    /// <summary>Whether the compilation calls <c>AddSimpleMediatorGenerated</c> or exports a module; diagnostics are only reported then.</summary>
    public bool UsesGeneratedEntryPoint { get; }

    /// <summary>When non-null, the compilation carries <c>[assembly: GenerateSimpleMediatorModule]</c> and exports a public module populator with this suffix.</summary>
    public string? ExportedModuleName { get; }

    /// <summary>Fully qualified calls to referenced assemblies' <c>SimpleMediatorGeneratedModule.Populate...</c> methods.</summary>
    public IReadOnlyList<string> ChainedModuleCalls { get; }

    /// <summary>Expressions evaluating to each scanned <see cref="System.Reflection.Assembly"/>.</summary>
    public IReadOnlyList<string> Assemblies { get; }

    public IReadOnlyList<(string Request, string Response)> Requests { get; }

    public IReadOnlyList<(string Request, string Response)> StreamRequests { get; }

    public IReadOnlyList<string> Notifications { get; }

    public IReadOnlyList<Registration> Handlers { get; }

    public IReadOnlyList<Registration> Behaviors { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool Equals(RegistrationModel? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return UsesGeneratedEntryPoint == other.UsesGeneratedEntryPoint
               && string.Equals(ExportedModuleName, other.ExportedModuleName, StringComparison.Ordinal)
               && SequenceEqual(ChainedModuleCalls, other.ChainedModuleCalls)
               && SequenceEqual(Assemblies, other.Assemblies)
               && SequenceEqual(Requests, other.Requests)
               && SequenceEqual(StreamRequests, other.StreamRequests)
               && SequenceEqual(Notifications, other.Notifications)
               && SequenceEqual(Handlers, other.Handlers)
               && SequenceEqual(Behaviors, other.Behaviors)
               && SequenceEqual(Diagnostics, other.Diagnostics);
    }

    public override bool Equals(object? obj) => Equals(obj as RegistrationModel);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = UsesGeneratedEntryPoint ? 1 : 0;
            hash = (hash * 397) ^ (ExportedModuleName is null ? 0 : StringComparer.Ordinal.GetHashCode(ExportedModuleName));
            hash = (hash * 397) ^ ChainedModuleCalls.Count;
            hash = (hash * 397) ^ Assemblies.Count;
            hash = (hash * 397) ^ Requests.Count;
            hash = (hash * 397) ^ StreamRequests.Count;
            hash = (hash * 397) ^ Notifications.Count;
            hash = (hash * 397) ^ Handlers.Count;
            hash = (hash * 397) ^ Behaviors.Count;
            hash = (hash * 397) ^ Diagnostics.Count;
            return hash;
        }
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A closed service/implementation pair and the key type the runtime matches it against.</summary>
internal readonly struct Registration : IEquatable<Registration>
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

    public bool Equals(Registration other)
        => string.Equals(Service, other.Service, StringComparison.Ordinal)
           && string.Equals(Implementation, other.Implementation, StringComparison.Ordinal)
           && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Registration other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(Service);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Implementation);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Key);
            return hash;
        }
    }
}
