using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public record FaultyRequest(string Message) : IRequest<string>;

public class FaultyHandler : IRequestHandler<FaultyRequest, string>
{
    public Task<string> Handle(FaultyRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException($"handler blew up on '{request.Message}'");
}

// Recovers the request by supplying a fallback response instead of letting the throw escape.
public class FaultyExceptionHandler : IRequestExceptionHandler<FaultyRequest, string>
{
    public Task Handle(FaultyRequest request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        state.SetHandled($"Recovered from: {exception.Message}");
        return Task.CompletedTask;
    }
}
