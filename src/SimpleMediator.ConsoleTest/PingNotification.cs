using SimpleMediator.Interfaces;

namespace SimpleMediator.ConsoleTest;

public class PingNotification : INotification
{
    public string Message { get; set; } = string.Empty;
}

public class PingNotificationHandler : INotificationHandler<PingNotification>
{
    public Task Handle(PingNotification notification, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Handler 1] Received notification: {notification.Message}");
        return Task.CompletedTask;
    }
}

