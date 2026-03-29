namespace SimpleMediator.Interfaces;

public interface IOrderedPipelineBehavior
{
    int Order { get; }
}

public interface IPipelineBehavior<in TRequest, TResponse> : IOrderedPipelineBehavior
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
