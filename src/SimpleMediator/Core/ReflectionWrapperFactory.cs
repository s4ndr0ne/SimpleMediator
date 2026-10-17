using System.Diagnostics.CodeAnalysis;

namespace SimpleMediator.Core;

/// <summary>
/// Builds dispatch wrappers by closing the generic wrapper types at runtime. This is the dispatch
/// path of the reflection-based registration; source-generated registrations never reach it.
/// </summary>
internal static class ReflectionWrapperFactory
{
    // The trim/AOT requirement of this path is declared once, at the composition root:
    // AddSimpleMediator carries [RequiresUnreferencedCode]/[RequiresDynamicCode], so an application
    // that can reach reflection dispatch has already been warned there. The only other way in is a
    // Mediator constructed from a provider without any SimpleMediator registration; its constructor
    // refuses that under Native AOT (RuntimeFeature.IsDynamicCodeSupported). AddSimpleMediatorGenerated
    // disables this path (AllowsReflectionDispatch).
    internal const string Justification =
        "Reflection dispatch is only reachable after the reflection-based AddSimpleMediator, which is annotated with " +
        "RequiresUnreferencedCode/RequiresDynamicCode, or from a Mediator constructed without any registration, which " +
        "the Mediator constructor rejects when dynamic code is not supported. " +
        "Source-generated registrations resolve every wrapper from the generated table.";

    internal static readonly Func<(Type Request, Type Response), object> Request = CreateRequestWrapper;
    internal static readonly Func<(Type Request, Type Response), object> StreamRequest = CreateStreamRequestWrapper;
    internal static readonly Func<Type, object> Notification = CreateNotificationWrapper;

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers", Justification = Justification)]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RequestHandlerWrapperImpl<,>))]
    internal static object CreateRequestWrapper((Type Request, Type Response) key, Configuration.MediatorConfiguration? configuration)
        => Activator.CreateInstance(typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(key.Request, key.Response), configuration)!;

    private static object CreateRequestWrapper((Type Request, Type Response) key)
        => CreateRequestWrapper(key, configuration: null);

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers", Justification = Justification)]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(StreamRequestHandlerWrapperImpl<,>))]
    internal static object CreateStreamRequestWrapper((Type Request, Type Response) key, Configuration.MediatorConfiguration? configuration)
        => Activator.CreateInstance(typeof(StreamRequestHandlerWrapperImpl<,>).MakeGenericType(key.Request, key.Response), configuration)!;

    private static object CreateStreamRequestWrapper((Type Request, Type Response) key)
        => CreateStreamRequestWrapper(key, configuration: null);

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers", Justification = Justification)]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(NotificationHandlerWrapperImpl<>))]
    internal static object CreateNotificationWrapper(Type notificationType, Configuration.MediatorConfiguration? configuration)
        => Activator.CreateInstance(typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(notificationType), configuration)!;

    private static object CreateNotificationWrapper(Type notificationType)
        => CreateNotificationWrapper(notificationType, configuration: null);
}
