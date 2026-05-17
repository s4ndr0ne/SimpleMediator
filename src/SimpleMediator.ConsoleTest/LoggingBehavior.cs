using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public int Order => 0;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Behavior] Before handling {typeof(TRequest).Name}");
        var response = await next(cancellationToken);
        Console.WriteLine($"[Behavior] After handling {typeof(TRequest).Name}");
        return response;
    }
}
