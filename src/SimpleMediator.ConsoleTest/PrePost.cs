using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class PrePostRequest : IRequest<string>
{
    public string Message { get; set; } = string.Empty;
}

public class PrePostRequestHandler : IRequestHandler<PrePostRequest, string>
{
    public Task<string> Handle(PrePostRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Handler: processing {request.Message}");
        return Task.FromResult($"Handled: {request.Message}");
    }
}

public class ConsolePreHandler : IPreRequestHandler<PrePostRequest, string>
{
    public Task Handle(PrePostRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"PreHandler: {request.Message}");
        return Task.CompletedTask;
    }
}

public class ConsolePostHandler : IPostRequestHandler<PrePostRequest, string>
{
    public Task Handle(PrePostRequest request, string response, CancellationToken cancellationToken)
    {
        Console.WriteLine($"PostHandler: {request.Message} -> {response}");
        return Task.CompletedTask;
    }
}
