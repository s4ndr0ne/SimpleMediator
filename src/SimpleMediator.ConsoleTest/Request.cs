using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class Request : IRequest<Response>
{
    public string? RequestMessage { get; set; } = string.Empty;
}

public class Response
{
    public string? ResponseMessage { get; set; }
}

public class RequestHandler : IRequestHandler<Request, Response>
{  
    public Task<Response> Handle(Request request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Response() { ResponseMessage = request.RequestMessage});
    }
}