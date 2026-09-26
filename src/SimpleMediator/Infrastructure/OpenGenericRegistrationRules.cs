using System.Diagnostics.CodeAnalysis;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// Checks whether an open-generic implementation follows the type-parameter mapping
/// required by Microsoft's native open-generic service registration.
/// </summary>
internal static class OpenGenericRegistrationRules
{
    private static readonly Type[] SupportedHandlerInterfaces =
    [
        typeof(IRequestHandler<,>),
        typeof(INotificationHandler<>),
        typeof(IPreRequestHandler<,>),
        typeof(IPostRequestHandler<,>),
        typeof(IRequestExceptionHandler<,>)
    ];

    internal static bool IsSupportedOpenGenericService(Type serviceTypeDefinition)
        => serviceTypeDefinition == typeof(IRequestHandler<,>)
           || serviceTypeDefinition == typeof(INotificationHandler<>)
           || serviceTypeDefinition == typeof(IPreRequestHandler<,>)
           || serviceTypeDefinition == typeof(IPostRequestHandler<,>)
           || serviceTypeDefinition == typeof(IRequestExceptionHandler<,>)
           || serviceTypeDefinition == typeof(IPipelineBehavior<,>);

    internal static Type? FindImplementedInterface(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType,
        Type serviceTypeDefinition)
        => implementationType
            .GetInterfaces()
            .FirstOrDefault(implementedInterface =>
                implementedInterface.IsGenericType &&
                implementedInterface.GetGenericTypeDefinition() == serviceTypeDefinition);

    internal static bool CanRegisterWithNativeResolution(
        Type implementationType,
        Type implementedInterface,
        Type serviceTypeDefinition)
    {
        if (!implementedInterface.IsGenericType ||
            implementedInterface.GetGenericTypeDefinition() != serviceTypeDefinition)
        {
            return false;
        }

        var implementationArguments = implementationType.GetGenericArguments();
        var serviceArguments = implementedInterface.GetGenericArguments();

        return serviceArguments.Length == implementationArguments.Length
               && serviceArguments.SequenceEqual(implementationArguments);
    }

    internal static bool IsClosedRequestHandler(Type serviceType)
        => serviceType.IsGenericType
           && !serviceType.ContainsGenericParameters
           && serviceType.GetGenericTypeDefinition() == typeof(IRequestHandler<,>);

    internal static bool IsPipelineBehaviorDefinition(Type serviceTypeDefinition)
        => serviceTypeDefinition == typeof(IPipelineBehavior<,>);

    internal static bool MatchesSupportedHandlerInterface(Type genericTypeDefinition)
        => SupportedHandlerInterfaces.Contains(genericTypeDefinition);

    internal static bool ImplementsPipelineBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type implementationType)
        => implementationType.GetInterfaces()
            .Any(implementedInterface =>
                implementedInterface.IsGenericType &&
                IsPipelineBehaviorDefinition(implementedInterface.GetGenericTypeDefinition()));
}
