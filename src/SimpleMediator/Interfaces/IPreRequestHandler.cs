namespace SimpleMediator.Interfaces;

public interface IPreRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
