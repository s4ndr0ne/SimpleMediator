using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// Argument guards shared by every target framework. netstandard2.0 has no
/// <c>ArgumentNullException.ThrowIfNull</c> or <c>ArgumentOutOfRangeException.ThrowIfNegativeOrZero</c>,
/// so call sites go through this type instead of the framework methods.
/// </summary>
internal static class ThrowHelper
{
    public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
#if NETSTANDARD2_0
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
#else
        ArgumentNullException.ThrowIfNull(argument, paramName);
#endif
    }

    public static void ThrowIfNegativeOrZero(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
#if NETSTANDARD2_0
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be greater than zero.");
        }
#else
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, paramName);
#endif
    }

    // ObjectDisposedException.ThrowIf does not exist on netstandard2.0.
    public static void ThrowIfDisposed(bool condition, object instance)
    {
#if NETSTANDARD2_0
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().ToString());
        }
#else
        ObjectDisposedException.ThrowIf(condition, instance);
#endif
    }
}
