namespace SimpleMediator.Interfaces;

public interface IRequest<TResponse>
{
}

public interface IRequest : IRequest<Unit>
{
}

public struct Unit
{
    public static readonly Unit Value = new Unit();
    public static Task<Unit> Task { get; } = System.Threading.Tasks.Task.FromResult(Value);
}
