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
    public override async Task Handle(INotification notification, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>().ToList();

        if (handlers.Count == 0)
            return;

        if (handlers.Count == 1)
        {
            await handlers[0].Handle((TNotification)notification, cancellationToken);
            return;
        }

        var tasks = handlers.Select(h => h.Handle((TNotification)notification, cancellationToken));
        await Task.WhenAll(tasks);
    }
}