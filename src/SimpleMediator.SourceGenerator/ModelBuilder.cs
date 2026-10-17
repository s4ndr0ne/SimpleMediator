using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimpleMediator.SourceGenerator;

/// <summary>
/// Reproduces the runtime assembly scanner at compile time and closes every open-generic handler
/// and behavior over the requests and notifications it can see.
/// </summary>
internal sealed class ModelBuilder
{
    private const string EntryPointTypeName = "SimpleMediatorGeneratedExtensions";
    private const string CurrentAssemblyExpression = "typeof(global::SimpleMediator." + EntryPointTypeName + ").Assembly";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly Compilation _compilation;
    private readonly KnownSymbols _known;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Diagnostic> _diagnostics = [];

    // Scanned assemblies in discovery order (current assembly first) with the expression that loads them.
    private readonly Dictionary<IAssemblySymbol, string> _assemblies = new(SymbolEqualityComparer.Default);
    private readonly List<IAssemblySymbol> _assemblyOrder = [];
    // The current assembly is always scanned for message types, but handler diagnostics should only
    // describe assemblies selected by RegisterAssembly. Arbitrary runtime filters cannot be
    // evaluated here, so duplicate diagnostics only use unfiltered registrations.
    private readonly HashSet<IAssemblySymbol> _registeredAssemblies = new(SymbolEqualityComparer.Default);
    private readonly HashSet<IAssemblySymbol> _assembliesWithFilters = new(SymbolEqualityComparer.Default);

    private readonly Dictionary<string, (INamedTypeSymbol Request, ITypeSymbol Response)> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (INamedTypeSymbol Request, ITypeSymbol Response)> _streamRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol> _notifications = new(StringComparer.Ordinal);
    private readonly HashSet<INamedTypeSymbol> _reportedInaccessible = new(SymbolEqualityComparer.Default);

    // Handler types per scanned assembly; behaviors across all sources (the runtime selects by AddBehavior).
    private readonly Dictionary<IAssemblySymbol, List<INamedTypeSymbol>> _handlerTypes = new(SymbolEqualityComparer.Default);
    private readonly List<INamedTypeSymbol> _behaviorTypes = [];
    private readonly HashSet<INamedTypeSymbol> _behaviorSet = new(SymbolEqualityComparer.Default);
    private readonly List<string> _chainedModuleCalls = [];

    private bool _usesGeneratedEntryPoint;
    private string? _exportedModuleName;

    public ModelBuilder(Compilation compilation, KnownSymbols known, CancellationToken cancellationToken)
    {
        _compilation = compilation;
        _known = known;
        _cancellationToken = cancellationToken;
    }

    public RegistrationModel Build(ImmutableArray<InvocationExpressionSyntax> invocations)
    {
        AddAssembly(_compilation.Assembly, CurrentAssemblyExpression);

        if (TryGetModuleName(_compilation.Assembly, out var exportedModuleName))
        {
            _exportedModuleName = exportedModuleName;
            _usesGeneratedEntryPoint = true;
            _registeredAssemblies.Add(_compilation.Assembly);
        }

        var messageCandidates = new List<(ITypeSymbol Type, Location Location)>();
        foreach (var invocation in invocations)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            AnalyzeInvocation(invocation, messageCandidates);
        }

        // Index every assembly before closing anything: requests discovered in one assembly may
        // close open-generic handlers declared in another.
        foreach (var assembly in _assemblyOrder)
        {
            ScanAssembly(assembly);
        }

        foreach (var (type, location) in messageCandidates)
        {
            if (ContainsTypeParameters(type))
            {
                _diagnostics.Add(Diagnostic.Create(Diagnostics.OpenGenericMessage, location, type.ToDisplayString()));
                continue;
            }

            AddMessage(type);
        }

        var handlers = BuildHandlers();
        var behaviors = BuildBehaviors();

        var requests = _requests.Values
            .Where(pair => IsAccessible(pair.Request, report: true) && IsAccessible(pair.Response, report: false))
            .Select(pair => (Request: Render(pair.Request), Response: Render(pair.Response)))
            .Distinct()
            .OrderBy(pair => pair.Request, StringComparer.Ordinal)
            .ThenBy(pair => pair.Response, StringComparer.Ordinal)
            .ToList();

        var streamRequests = _streamRequests.Values
            .Where(pair => IsAccessible(pair.Request, report: true) && IsAccessible(pair.Response, report: false))
            .Select(pair => (Request: Render(pair.Request), Response: Render(pair.Response)))
            .Distinct()
            .OrderBy(pair => pair.Request, StringComparer.Ordinal)
            .ThenBy(pair => pair.Response, StringComparer.Ordinal)
            .ToList();

        var notifications = _notifications.Values
            .Where(notification => IsAccessible(notification, report: true))
            .Select(Render)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return new RegistrationModel(
            _usesGeneratedEntryPoint,
            _exportedModuleName,
            _chainedModuleCalls,
            _assemblyOrder.Select(assembly => _assemblies[assembly]).ToList(),
            requests,
            streamRequests,
            notifications,
            handlers,
            behaviors,
            _diagnostics);
    }

    private bool TryGetModuleName(IAssemblySymbol assembly, out string? moduleName)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (KnownSymbols.Equal(attribute.AttributeClass, _known.ModuleAttribute))
            {
                string? rawName = null;
                if (attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is string arg &&
                    !string.IsNullOrWhiteSpace(arg))
                {
                    rawName = arg;
                }

                moduleName = SanitizeIdentifier(rawName ?? assembly.Name);
                return true;
            }
        }

        moduleName = null;
        return false;
    }

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private void AnalyzeInvocation(
        InvocationExpressionSyntax invocation,
        List<(ITypeSymbol Type, Location Location)> messageCandidates)
    {
        var semanticModel = _compilation.GetSemanticModel(invocation.SyntaxTree);
        if (semanticModel.GetSymbolInfo(invocation, _cancellationToken).Symbol is not IMethodSymbol method)
        {
            return;
        }

        var firstArgument = invocation.ArgumentList.Arguments.Count > 0
            ? invocation.ArgumentList.Arguments[0].Expression
            : null;

        switch (method.Name)
        {
            case "AddSimpleMediatorGenerated":
                if (method.ContainingType is { Name: EntryPointTypeName } entryPoint &&
                    entryPoint.ContainingNamespace.ToDisplayString() == "SimpleMediator" &&
                    KnownSymbols.Equal(entryPoint.ContainingAssembly, _compilation.Assembly))
                {
                    _usesGeneratedEntryPoint = true;
                }

                break;

            case "RegisterAssembly" when KnownSymbols.Equal(method.ContainingType, _known.Options) && firstArgument is not null:
                if (!TryAddRegisteredAssembly(semanticModel, firstArgument, out var registeredAssembly))
                {
                    _diagnostics.Add(Diagnostic.Create(Diagnostics.AssemblyNotResolvable, firstArgument.GetLocation()));
                }
                else if (registeredAssembly is not null && HasNonNullAssemblyFilter(semanticModel, invocation, method))
                {
                    _assembliesWithFilters.Add(registeredAssembly);
                }

                break;

            case "AddBehavior" or "AddStreamBehavior" or "AddNotificationBehavior"
                when KnownSymbols.Equal(method.ContainingType, _known.Options) && firstArgument is not null:
                if (firstArgument is TypeOfExpressionSyntax typeOf &&
                    semanticModel.GetTypeInfo(typeOf.Type, _cancellationToken).Type is INamedTypeSymbol behavior)
                {
                    // typeof(B<,>) binds to the unbound form; the definition carries the type parameters.
                    var definition = behavior.IsUnboundGenericType ? behavior.OriginalDefinition : behavior;
                    if (IsAccessible(definition, report: true, typeOf.GetLocation()))
                    {
                        AddBehaviorType(definition);
                    }
                }
                else
                {
                    _diagnostics.Add(Diagnostic.Create(Diagnostics.BehaviorNotTypeOf, firstArgument.GetLocation()));
                }

                break;

            case "Send" when IsMemberOf(method, _known.Sender) && firstArgument is not null:
            case "CreateStream" when IsMemberOf(method, _known.Sender) && firstArgument is not null:
            case "Publish" when IsMemberOf(method, _known.Publisher) && firstArgument is not null:
                if (semanticModel.GetTypeInfo(firstArgument, _cancellationToken).Type is { } messageType)
                {
                    messageCandidates.Add((messageType, firstArgument.GetLocation()));
                }

                break;
        }
    }

    private static bool IsMemberOf(IMethodSymbol method, INamedTypeSymbol contract)
        => method.ContainingType is { } containingType &&
           (KnownSymbols.Equal(containingType, contract) ||
            containingType.AllInterfaces.Any(implemented => KnownSymbols.Equal(implemented, contract)));

    private bool TryAddRegisteredAssembly(
        SemanticModel semanticModel,
        ExpressionSyntax argument,
        out IAssemblySymbol? registeredAssembly)
    {
        registeredAssembly = null;

        // Assembly.GetExecutingAssembly() is the assembly being compiled.
        if (argument is InvocationExpressionSyntax call &&
            semanticModel.GetSymbolInfo(call, _cancellationToken).Symbol is IMethodSymbol { Name: "GetExecutingAssembly" } getExecuting &&
            KnownSymbols.Equal(getExecuting.ContainingType, _known.Assembly))
        {
            registeredAssembly = _compilation.Assembly;
            _registeredAssemblies.Add(registeredAssembly);
            return true;
        }

        // typeof(T).Assembly or typeof(T).GetTypeInfo().Assembly
        if (argument is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assembly" } assemblyAccess ||
            semanticModel.GetSymbolInfo(assemblyAccess, _cancellationToken).Symbol is not IPropertySymbol assemblyProperty ||
            !KnownSymbols.Equal(assemblyProperty.Type, _known.Assembly))
        {
            return false;
        }

        var target = assemblyAccess.Expression;
        if (target is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetTypeInfo" } getTypeInfo })
        {
            target = getTypeInfo.Expression;
        }

        if (target is not TypeOfExpressionSyntax typeOf ||
            semanticModel.GetTypeInfo(typeOf.Type, _cancellationToken).Type is not INamedTypeSymbol anchor)
        {
            return false;
        }

        var assembly = anchor.OriginalDefinition.ContainingAssembly;
        if (assembly is null)
        {
            return false;
        }

        if (KnownSymbols.Equal(assembly, _compilation.Assembly))
        {
            registeredAssembly = assembly;
            _registeredAssemblies.Add(assembly);
            return true;
        }

        if (_assemblies.ContainsKey(assembly))
        {
            registeredAssembly = assembly;
            _registeredAssemblies.Add(assembly);
            return true;
        }

        var anchorExpression = FindAnchor(anchor.OriginalDefinition, assembly);
        if (anchorExpression is null)
        {
            return false;
        }

        AddAssembly(assembly, "typeof(" + anchorExpression + ").Assembly");
        registeredAssembly = assembly;
        _registeredAssemblies.Add(assembly);
        return true;
    }

    private bool HasNonNullAssemblyFilter(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        var filterParameter = method.Parameters.FirstOrDefault(parameter => parameter.Name == "filter");
        if (filterParameter is null)
        {
            return false;
        }

        for (var index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            var argument = invocation.ArgumentList.Arguments[index];
            var parameterOrdinal = argument.NameColon is { } nameColon
                ? method.Parameters.FirstOrDefault(parameter => parameter.Name == nameColon.Name.Identifier.ValueText)?.Ordinal ?? -1
                : index;

            if (parameterOrdinal != filterParameter.Ordinal)
            {
                continue;
            }

            var constant = semanticModel.GetConstantValue(argument.Expression, _cancellationToken);
            return !(constant.HasValue && constant.Value is null);
        }

        return false;
    }

    // The type named at the call site may be inaccessible from the generated class (for example a
    // private nested type); any accessible type of the same assembly identifies it just as well.
    private string? FindAnchor(INamedTypeSymbol preferred, IAssemblySymbol assembly)
    {
        if (IsAccessible(preferred, report: false))
        {
            return RenderTypeOfOperand(preferred);
        }

        var fallback = EnumerateTypes(assembly.GlobalNamespace)
            .FirstOrDefault(type => type.ContainingType is null && IsAccessible(type, report: false));
        return fallback is null ? null : RenderTypeOfOperand(fallback);
    }

    private void AddAssembly(IAssemblySymbol assembly, string expression)
    {
        if (!_assemblies.ContainsKey(assembly))
        {
            _assemblies.Add(assembly, expression);
            _assemblyOrder.Add(assembly);
        }
    }

    private void ScanAssembly(IAssemblySymbol assembly)
    {
        var handlerTypes = new List<INamedTypeSymbol>();
        _handlerTypes[assembly] = handlerTypes;

        // If a referenced assembly has [assembly: GenerateSimpleMediatorModule], its own generated
        // SimpleMediatorGeneratedModule_{Name}.Populate method already registers its public AND internal
        // handlers, messages, and assembly entry. Delegate to it instead of duplicating registrations here!
        if (!KnownSymbols.Equal(assembly, _compilation.Assembly) &&
            TryGetModuleName(assembly, out var referencedModuleName) &&
            referencedModuleName is not null)
        {
            var call = "global::SimpleMediator.SimpleMediatorGeneratedModule_" + referencedModuleName + ".Populate";
            if (!_chainedModuleCalls.Contains(call))
            {
                _chainedModuleCalls.Add(call);
            }

            // Still discover public messages from the module so open-generic handlers/behaviors in the
            // host project can close over them.
            foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (type.IsAbstract || type.IsStatic || type.IsImplicitlyDeclared || type.Name.StartsWith("<", StringComparison.Ordinal) ||
                    HasGenericContainingType(type))
                {
                    continue;
                }

                if (type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsGenericType)
                {
                    AddMessage(type);
                }

                if (type.TypeKind == TypeKind.Class &&
                    type.AllInterfaces.Any(implemented => _known.IsBehaviorInterface(implemented.OriginalDefinition)) &&
                    IsAccessible(type, report: false))
                {
                    AddBehaviorType(type);
                }
            }

            return;
        }

        foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
        {
            _cancellationToken.ThrowIfCancellationRequested();

            // Same exclusions as the runtime scanner: nothing abstract, static or compiler-generated,
            // and nothing nested in a generic type (it can never be closed).
            if (type.IsAbstract || type.IsStatic || type.IsImplicitlyDeclared || type.Name.StartsWith("<", StringComparison.Ordinal) ||
                HasGenericContainingType(type))
            {
                continue;
            }

            if (type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsGenericType)
            {
                AddMessage(type);
            }

            if (type.TypeKind != TypeKind.Class)
            {
                continue;
            }

            var isHandler = type.AllInterfaces.Any(implemented => _known.IsHandlerInterface(implemented.OriginalDefinition));
            var isBehavior = type.AllInterfaces.Any(implemented => _known.IsBehaviorInterface(implemented.OriginalDefinition));

            if (isHandler && IsAccessible(type, report: _registeredAssemblies.Contains(assembly)))
            {
                handlerTypes.Add(type);

                if (!type.IsGenericType)
                {
                    // A closed handler also identifies the message it handles.
                    foreach (var implemented in type.AllInterfaces.Where(i => _known.IsHandlerInterface(i.OriginalDefinition)))
                    {
                        AddMessage(implemented.TypeArguments[0]);
                    }
                }
            }

            if (isBehavior && IsAccessible(type, report: false))
            {
                AddBehaviorType(type);
            }
        }
    }

    private void AddBehaviorType(INamedTypeSymbol behavior)
    {
        if (_behaviorSet.Add(behavior))
        {
            _behaviorTypes.Add(behavior);
        }
    }

    private void AddMessage(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false, IsStatic: false } message ||
            message.IsUnboundGenericType ||
            ContainsTypeParameters(message))
        {
            return;
        }

        foreach (var implemented in message.AllInterfaces)
        {
            if (KnownSymbols.Equal(implemented.OriginalDefinition, _known.Request))
            {
                var response = implemented.TypeArguments[0];
                var key = Render(message) + "|" + Render(response);
                if (!_requests.ContainsKey(key))
                {
                    _requests.Add(key, (message, response));
                }
            }
            else if (KnownSymbols.Equal(implemented.OriginalDefinition, _known.StreamRequest))
            {
                var response = implemented.TypeArguments[0];
                var key = Render(message) + "|" + Render(response);
                if (!_streamRequests.ContainsKey(key))
                {
                    _streamRequests.Add(key, (message, response));
                }
            }
            else if (KnownSymbols.Equal(implemented, _known.Notification))
            {
                _notifications[Render(message)] = message;
            }
        }
    }

    private List<Registration> BuildHandlers()
    {
        var registrations = new List<Registration>();
        var requestHandlers = new Dictionary<string, (INamedTypeSymbol Service, List<(string Implementation, Location? Location, IAssemblySymbol Assembly)> Implementations)>(StringComparer.Ordinal);

        foreach (var assembly in _assemblyOrder)
        {
            // Ordinal full-name order, as the runtime scanner registers them: it decides the order in
            // which notification handlers run.
            foreach (var type in _handlerTypes[assembly].OrderBy(GetMetadataFullName, StringComparer.Ordinal))
            {
                var key = RenderTypeOfOperand(type);
                var closedRegistrations = new List<(INamedTypeSymbol Service, INamedTypeSymbol Implementation)>();

                foreach (var implemented in type.AllInterfaces.Where(i => _known.IsHandlerInterface(i.OriginalDefinition)))
                {
                    if (!type.IsGenericType)
                    {
                        closedRegistrations.Add((implemented, type));
                        continue;
                    }

                    closedRegistrations.AddRange(CloseOverMessages(type, implemented));
                }

                if (type.IsGenericType && closedRegistrations.Count == 0 &&
                    _registeredAssemblies.Contains(type.ContainingAssembly!))
                {
                    ReportUnused(type);
                }

                foreach (var (service, implementation) in closedRegistrations
                             .GroupBy(pair => (Service: Render(pair.Service), Implementation: Render(pair.Implementation)))
                             .Select(group => group.First())
                             .OrderBy(pair => Render(pair.Service), StringComparer.Ordinal))
                {
                    var renderedService = Render(service);
                    var renderedImplementation = Render(implementation);
                    registrations.Add(new Registration(renderedService, renderedImplementation, key));

                    if (KnownSymbols.Equal(service.OriginalDefinition, _known.RequestHandler) ||
                        KnownSymbols.Equal(service.OriginalDefinition, _known.StreamRequestHandler))
                    {
                        if (!requestHandlers.TryGetValue(renderedService, out var entry))
                        {
                            entry = (service, []);
                            requestHandlers.Add(renderedService, entry);
                        }

                        var implementations = entry.Implementations;

                        if (!implementations.Any(existing => existing.Implementation == renderedImplementation))
                        {
                            implementations.Add((renderedImplementation,
                                type.Locations.FirstOrDefault(location => location.IsInSource),
                                type.ContainingAssembly!));
                        }
                    }
                }
            }
        }

        foreach (var entry in requestHandlers
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Select(pair => pair.Value))
        {
            var reportableImplementations = entry.Implementations
                .Where(implementation => _registeredAssemblies.Contains(implementation.Assembly) &&
                                         !_assembliesWithFilters.Contains(implementation.Assembly))
                .ToList();
            if (reportableImplementations.Count < 2)
            {
                continue;
            }

            _diagnostics.Add(Diagnostic.Create(
                Diagnostics.DuplicateRequestHandler,
                reportableImplementations.Select(implementation => implementation.Location).FirstOrDefault(location => location is not null) ?? Location.None,
                entry.Service.TypeArguments[0].ToDisplayString(),
                entry.Service.TypeArguments[1].ToDisplayString(),
                string.Join(", ", reportableImplementations.Select(implementation => implementation.Implementation))));
        }

        return registrations;
    }

    private List<Registration> BuildBehaviors()
    {
        var registrations = new List<Registration>();

        foreach (var behavior in _behaviorTypes)
        {
            var key = RenderTypeOfOperand(behavior);
            var closedRegistrations = new List<(INamedTypeSymbol Service, INamedTypeSymbol Implementation)>();

            foreach (var implemented in behavior.AllInterfaces.Where(i => _known.IsBehaviorInterface(i.OriginalDefinition)))
            {
                if (!IsOpenDefinition(behavior))
                {
                    closedRegistrations.Add((implemented, behavior));
                    continue;
                }

                closedRegistrations.AddRange(CloseOverMessages(behavior, implemented));
            }

            if (IsOpenDefinition(behavior) && closedRegistrations.Count == 0)
            {
                ReportUnused(behavior);
            }

            registrations.AddRange(closedRegistrations
                .Select(pair => new Registration(Render(pair.Service), Render(pair.Implementation), key))
                .GroupBy(registration => registration.Service + "|" + registration.Implementation, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(registration => registration.Service, StringComparer.Ordinal));
        }

        return registrations
            .OrderBy(registration => registration.Key, StringComparer.Ordinal)
            .ThenBy(registration => registration.Service, StringComparer.Ordinal)
            .ToList();
    }

    private void ReportUnused(INamedTypeSymbol type)
    {
        if (type.Locations.FirstOrDefault(location => location.IsInSource) is { } location)
        {
            _diagnostics.Add(Diagnostic.Create(Diagnostics.OpenGenericUnused, location, type.ToDisplayString()));
        }
    }

    /// <summary>
    /// Closes <paramref name="definition"/> for every known message matching the interface pattern
    /// <paramref name="pattern"/> (expressed over the definition's own type parameters).
    /// </summary>
    private IEnumerable<(INamedTypeSymbol Service, INamedTypeSymbol Implementation)> CloseOverMessages(
        INamedTypeSymbol definition,
        INamedTypeSymbol pattern)
    {
        var patternDefinition = pattern.OriginalDefinition;
        IEnumerable<ITypeSymbol[]> targets;
        if (_known.IsRequestShapedInterface(patternDefinition))
        {
            targets = _requests.Values.Select(request => new[] { (ITypeSymbol)request.Request, request.Response });
        }
        else if (_known.IsStreamRequestShapedInterface(patternDefinition))
        {
            targets = _streamRequests.Values.Select(request => new[] { (ITypeSymbol)request.Request, request.Response });
        }
        else
        {
            targets = _notifications.Values.Select(notification => new[] { (ITypeSymbol)notification });
        }

        foreach (var arguments in targets)
        {
            var bindings = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            var matches = true;
            for (var i = 0; i < arguments.Length && matches; i++)
            {
                matches = Unify(pattern.TypeArguments[i], arguments[i], bindings);
            }

            if (!matches || definition.TypeParameters.Any(parameter => !bindings.ContainsKey(parameter)))
            {
                continue;
            }

            var typeArguments = definition.TypeParameters.Select(parameter => bindings[parameter]).ToArray();
            if (!SatisfiesConstraints(definition, typeArguments, bindings) ||
                !typeArguments.All(argument => IsAccessible(argument, report: false)))
            {
                continue;
            }

            yield return (patternDefinition.Construct(arguments), definition.Construct(typeArguments));
        }
    }

    private static bool Unify(ITypeSymbol pattern, ITypeSymbol actual, Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings)
    {
        switch (pattern)
        {
            case ITypeParameterSymbol parameter:
                if (bindings.TryGetValue(parameter, out var bound))
                {
                    return KnownSymbols.Equal(bound, actual);
                }

                bindings.Add(parameter, actual);
                return true;

            case IArrayTypeSymbol patternArray:
                return actual is IArrayTypeSymbol actualArray &&
                       patternArray.Rank == actualArray.Rank &&
                       Unify(patternArray.ElementType, actualArray.ElementType, bindings);

            case INamedTypeSymbol { IsGenericType: true } patternNamed:
                if (actual is not INamedTypeSymbol { IsGenericType: true } actualNamed ||
                    !KnownSymbols.Equal(patternNamed.OriginalDefinition, actualNamed.OriginalDefinition))
                {
                    return false;
                }

                for (var i = 0; i < patternNamed.TypeArguments.Length; i++)
                {
                    if (!Unify(patternNamed.TypeArguments[i], actualNamed.TypeArguments[i], bindings))
                    {
                        return false;
                    }
                }

                // Equal definitions imply equal containers unless the container is itself generic.
                return patternNamed.ContainingType is not { IsGenericType: true } patternContainer ||
                       (actualNamed.ContainingType is { } actualContainer && Unify(patternContainer, actualContainer, bindings));

            default:
                return KnownSymbols.Equal(pattern, actual);
        }
    }

    // Roslyn has no public API to validate a construction, so the C# constraint rules are applied
    // here; Microsoft DI would otherwise reject the closed type at runtime.
    private bool SatisfiesConstraints(
        INamedTypeSymbol definition,
        ITypeSymbol[] typeArguments,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings)
    {
        if (_compilation is not CSharpCompilation compilation)
        {
            return false;
        }

        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            var parameter = definition.TypeParameters[i];
            var argument = typeArguments[i];

            if (argument.IsRefLikeType || argument is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
            {
                return false;
            }

            if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType)
            {
                return false;
            }

            if (parameter.HasValueTypeConstraint &&
                (!argument.IsValueType || argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            {
                return false;
            }

            if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType)
            {
                return false;
            }

            if (parameter.HasConstructorConstraint && !HasPublicParameterlessConstructor(argument))
            {
                return false;
            }

            foreach (var constraint in parameter.ConstraintTypes)
            {
                var substituted = Substitute(constraint, bindings);
                var conversion = compilation.ClassifyConversion(argument, substituted);
                if (!conversion.IsIdentity && !(conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasPublicParameterlessConstructor(ITypeSymbol type)
        => type.IsValueType ||
           (type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named &&
            named.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public));

    private ITypeSymbol Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> bindings)
        => type switch
        {
            ITypeParameterSymbol parameter when bindings.TryGetValue(parameter, out var bound) => bound,
            IArrayTypeSymbol array => _compilation.CreateArrayTypeSymbol(Substitute(array.ElementType, bindings), array.Rank),
            INamedTypeSymbol { IsGenericType: true } named =>
                named.ConstructedFrom.Construct(named.TypeArguments.Select(argument => Substitute(argument, bindings)).ToArray()),
            _ => type,
        };

    private bool IsAccessible(ITypeSymbol type, bool report, Location? location = null)
    {
        var accessible = IsAccessibleCore(type);
        if (!accessible && report && type is INamedTypeSymbol named &&
            _reportedInaccessible.Add(named.OriginalDefinition))
        {
            var reportLocation = location ?? named.Locations.FirstOrDefault(candidate => candidate.IsInSource);

            // Inaccessible metadata types are simply invisible to the generator; only source types are reported.
            if (reportLocation is not null)
            {
                var reason = named.IsFileLocal ? "file-local type" : "it is " + DescribeAccessibility(named);
                _diagnostics.Add(Diagnostic.Create(Diagnostics.InaccessibleType, reportLocation, named.ToDisplayString(), reason));
            }
        }

        return accessible;
    }

    private static string DescribeAccessibility(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            var keyword = current.DeclaredAccessibility switch
            {
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => null,
            };

            if (keyword is not null)
            {
                return KnownSymbols.Equal(current, type) ? keyword : "nested in " + keyword + " '" + current.Name + "'";
            }
        }

        return "not visible from this assembly";
    }

    private bool IsAccessibleCore(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => IsAccessibleCore(array.ElementType),
            // Only a definition's own parameters reach here (constructed types are fully closed).
            ITypeParameterSymbol => true,
            INamedTypeSymbol named =>
                named.TypeKind != TypeKind.Error &&
                !named.IsAnonymousType &&
                !IsFileLocal(named) &&
                _compilation.IsSymbolAccessibleWithin(named.OriginalDefinition, _compilation.Assembly) &&
                named.TypeArguments.All(IsAccessibleCore),
            _ => false,
        };

    private static bool IsFileLocal(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGenericContainingType(INamedTypeSymbol type)
    {
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTypeParameters(ITypeSymbol type)
        => type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameters(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameters) ||
                                      (named.ContainingType is not null && ContainsTypeParameters(named.ContainingType)),
            _ => false,
        };

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol root)
    {
        var namespaces = new Stack<INamespaceSymbol>();
        namespaces.Push(root);
        var types = new Stack<INamedTypeSymbol>();

        while (namespaces.Count > 0)
        {
            var current = namespaces.Pop();
            foreach (var nested in current.GetNamespaceMembers())
            {
                namespaces.Push(nested);
            }

            foreach (var type in current.GetTypeMembers())
            {
                types.Push(type);
            }

            while (types.Count > 0)
            {
                var type = types.Pop();
                yield return type;
                foreach (var nested in type.GetTypeMembers())
                {
                    types.Push(nested);
                }
            }
        }
    }

    /// <summary>Equivalent of <see cref="Type.FullName"/> for a type definition (<c>NS.Outer+Inner`1</c>).</summary>
    private static string GetMetadataFullName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            name = current.MetadataName + "+" + name;
            type = current;
        }

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + name
            : name;
    }

    private static string Render(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    /// <summary>Renders a type definition as a <c>typeof</c> operand: open generics as <c>global::NS.T&lt;,&gt;</c>.</summary>
    private static string RenderTypeOfOperand(INamedTypeSymbol type)
        => IsOpenDefinition(type)
            ? type.ConstructUnboundGenericType().ToDisplayString(TypeFormat)
            : Render(type);

    private static bool IsOpenDefinition(INamedTypeSymbol type)
        => type.IsGenericType && KnownSymbols.Equal(type, type.OriginalDefinition);
}
