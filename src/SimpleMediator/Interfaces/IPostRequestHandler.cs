namespace SimpleMediator.Interfaces;

public interface IPostRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Handle(TRequest request, TResponse response, CancellationToken cancellationToken);
}
