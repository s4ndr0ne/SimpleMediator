using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

// A generic request: the SAME open-generic handler below serves every closed T.
public record EchoRequest<T>(T Value) : IRequest<T>;

public class EchoHandler<T> : IRequestHandler<EchoRequest<T>, T>
{
    public Task<T> Handle(EchoRequest<T> request, CancellationToken cancellationToken)
        => Task.FromResult(request.Value);
}
