namespace SimpleMediator.Interfaces;

public interface IRequest<TResponse>
{
}

public interface IRequest : IRequest<Unit>
{
}

public struct Unit : IEquatable<Unit>
{
    public static readonly Unit Value;
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);

    public bool Equals(Unit other) => true;

    public override bool Equals(object? obj) => obj is Unit;

    public override int GetHashCode() => 0;

    public static bool operator ==(Unit left, Unit right) => true;

    public static bool operator !=(Unit left, Unit right) => false;
}
