using SimpleMediator.Interfaces;
using Microsoft.Extensions.DependencyInjection;
namespace SimpleMediator.Core;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();

        if (!handlers.Any())
        {
            return Task.CompletedTask;
        }

        var tasks = handlers.Select(h => h.Handle((TNotification)notification, cancellationToken));
        return Task.WhenAll(tasks);
    }
}