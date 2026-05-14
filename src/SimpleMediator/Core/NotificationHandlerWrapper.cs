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
        using var discoveryScope = serviceProvider.CreateScope();
        var handlers = discoveryScope.ServiceProvider.GetServices<INotificationHandler<TNotification>>().ToList();

        if (handlers.Count == 0)
            return;

        if (handlers.Count == 1)
        {
            await handlers[0].Handle((TNotification)notification, cancellationToken);
            return;
        }

        var tasks = Enumerable.Range(0, handlers.Count).Select(async index =>
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var scopedHandlers = scope.ServiceProvider.GetServices<INotificationHandler<TNotification>>().ToList();
            await scopedHandlers[index].Handle((TNotification)notification, cancellationToken);
        });

        await Task.WhenAll(tasks);
    }
}