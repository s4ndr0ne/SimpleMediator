namespace SimpleMediator.Interfaces;

/// <summary>
/// Represents a request with a response of type <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The type of response returned by the handler.</typeparam>
public interface IRequest<TResponse>
{
}

/// <summary>
/// Represents a request with no response value (returns <see cref="Unit"/>).
/// </summary>
public interface IRequest : IRequest<Unit>
{
}

/// <summary>
/// Represents a void or empty response type for requests without a meaningful return value.
/// </summary>
public readonly struct Unit : IEquatable<Unit>
{
    /// <summary>
    /// The single canonical value of <see cref="Unit"/>.
    /// </summary>
    public static readonly Unit Value;

    /// <summary>
    /// A completed <see cref="Task{Unit}"/> containing <see cref="Value"/>.
    /// </summary>
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);

    /// <summary>
    /// Indicates whether this instance is equal to another <see cref="Unit"/>.
    /// </summary>
    /// <param name="other">The other unit.</param>
    /// <returns>True always, as all unit values are equal.</returns>
    public bool Equals(Unit other) => true;

    /// <summary>
    /// Indicates whether this instance is equal to another object.
    /// </summary>
    /// <param name="obj">The object to compare.</param>
    /// <returns>True if the object is a <see cref="Unit"/>.</returns>
    public override bool Equals(object? obj) => obj is Unit;

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    /// <returns>Zero.</returns>
    public override int GetHashCode() => 0;

    /// <summary>
    /// Determines whether two <see cref="Unit"/> values are equal.
    /// </summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>
    /// Determines whether two <see cref="Unit"/> values are not equal.
    /// </summary>
    public static bool operator !=(Unit left, Unit right) => false;
}
