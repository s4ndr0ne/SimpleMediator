using System.Diagnostics.CodeAnalysis;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// Closes a non-native open-generic request-handler implementation against a concrete
/// (request, response) pair. Microsoft DI only supports open generics whose
/// implementation type parameters line up 1:1 with the service interface, so it
/// cannot resolve custom mappings such as
/// <c>EchoHandler&lt;T&gt; : IRequestHandler&lt;EchoRequest&lt;T&gt;, T&gt;</c>.
/// This matcher performs that unification by hand at request time.
/// </summary>
internal static class OpenGenericMatcher
{
    /// <summary>
    /// Attempts to build the closed implementation type that satisfies
    /// <c>IRequestHandler&lt;requestType, responseType&gt;</c> from the given open-generic
    /// implementation. Returns false when no implemented interface unifies or when the
    /// inferred type arguments violate the implementation's generic constraints.
    /// </summary>
    [RequiresUnreferencedCode(ServiceCollectionExtensions.ReflectionMessage)]
    [RequiresDynamicCode(ServiceCollectionExtensions.DynamicCodeMessage)]
    public static bool TryClose(Type openImplementation, Type requestType, Type responseType, out Type? closedImplementation)
    {
        closedImplementation = null;

        foreach (var iface in openImplementation.GetInterfaces())
        {
            if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
            {
                continue;
            }

            var pattern = iface.GetGenericArguments(); // e.g. [EchoRequest<T>, T]
            var bindings = new Dictionary<Type, Type>();

            if (!TryUnify(pattern[0], requestType, bindings) || !TryUnify(pattern[1], responseType, bindings))
            {
                continue;
            }

            var implParameters = openImplementation.GetGenericArguments();
            var typeArguments = new Type[implParameters.Length];
            var allBound = true;
            for (var i = 0; i < implParameters.Length; i++)
            {
                if (!bindings.TryGetValue(implParameters[i], out var bound))
                {
                    allBound = false;
                    break;
                }

                typeArguments[i] = bound;
            }

            if (!allBound)
            {
                continue;
            }

            Type candidate;
            try
            {
                candidate = openImplementation.MakeGenericType(typeArguments);
            }
            catch (ArgumentException)
            {
                // Inferred arguments violate a generic constraint on the implementation.
                continue;
            }

            var target = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
            if (target.IsAssignableFrom(candidate))
            {
                closedImplementation = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates that an open-generic request handler exposes enough information in its
    /// request/response interface pattern for <see cref="TryClose"/> to infer every
    /// implementation type parameter. Unsupported mappings are rejected during registration
    /// instead of being silently ignored.
    /// </summary>
    internal static bool CanInferOpenGenericRequestHandler(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type openImplementation)
    {
        var requestInterfaces = openImplementation
            .GetInterfaces()
            .Where(interfaceType =>
                interfaceType.IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .ToArray();

        if (requestInterfaces.Length == 0)
        {
            return false;
        }

        var implementationParameters = openImplementation.GetGenericArguments();
        foreach (var requestInterface in requestInterfaces)
        {
            var patternParameters = new HashSet<Type>();
            var requestPattern = requestInterface.GetGenericArguments()[0];
            var responsePattern = requestInterface.GetGenericArguments()[1];

            if (!TryCollectGenericParameters(requestPattern, patternParameters) ||
                !TryCollectGenericParameters(responsePattern, patternParameters))
            {
                return false;
            }

            if (implementationParameters.Any(parameter => !patternParameters.Contains(parameter)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCollectGenericParameters(Type pattern, ISet<Type> parameters)
    {
        if (pattern.IsGenericParameter)
        {
            parameters.Add(pattern);
            return true;
        }

        if (pattern.IsByRef || pattern.IsPointer)
        {
            return !pattern.ContainsGenericParameters;
        }

        if (pattern.IsArray)
        {
            return TryCollectGenericParameters(pattern.GetElementType()!, parameters);
        }

        if (pattern.IsGenericType)
        {
            foreach (var argument in pattern.GetGenericArguments())
            {
                if (!TryCollectGenericParameters(argument, parameters))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Structurally unifies an interface-argument <paramref name="pattern"/> (which may
    /// contain the implementation's generic parameters) against a fully-closed
    /// <paramref name="concrete"/> type, recording each generic-parameter binding.
    /// </summary>
    private static bool TryUnify(Type pattern, Type concrete, Dictionary<Type, Type> bindings)
    {
        if (pattern.IsGenericParameter)
        {
            if (bindings.TryGetValue(pattern, out var existing))
            {
                return existing == concrete;
            }

            bindings[pattern] = concrete;
            return true;
        }

        if (pattern.IsArray && concrete.IsArray && pattern.GetArrayRank() == concrete.GetArrayRank())
        {
            return TryUnify(pattern.GetElementType()!, concrete.GetElementType()!, bindings);
        }

        if (pattern.IsGenericType && concrete.IsGenericType)
        {
            if (pattern.GetGenericTypeDefinition() != concrete.GetGenericTypeDefinition())
            {
                return false;
            }

            var patternArgs = pattern.GetGenericArguments();
            var concreteArgs = concrete.GetGenericArguments();
            if (patternArgs.Length != concreteArgs.Length)
            {
                return false;
            }

            for (var i = 0; i < patternArgs.Length; i++)
            {
                if (!TryUnify(patternArgs[i], concreteArgs[i], bindings))
                {
                    return false;
                }
            }

            return true;
        }

        // A closed pattern must match exactly; a still-open pattern (e.g. an array of T)
        // that reached here cannot be unified by this simplified matcher.
        return !pattern.ContainsGenericParameters && pattern == concrete;
    }
}
