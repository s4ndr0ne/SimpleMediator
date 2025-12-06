using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class PrintRequest : IRequest
{
    public string Text { get; set; } = string.Empty;
}

public class PrintHandler : IRequestHandler<PrintRequest, Unit>
{
    public Task<Unit> Handle(PrintRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Printing: {request.Text}");
        return Unit.Task;
    }
}
