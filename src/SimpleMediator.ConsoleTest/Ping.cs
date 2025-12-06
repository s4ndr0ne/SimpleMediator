using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class PingRequest : IRequest<string>
{
    public string Message { get; set; } = string.Empty;
}

public class PingHandler : IRequestHandler<PingRequest, string>
{
    public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Pong: {request.Message}");
    }
}
