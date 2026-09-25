using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal static class RequestHandlerPipeline
{
    public static async Task<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IEnumerable<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IEnumerable<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        RequestHandlerDelegate<TResponse> handlerDelegate = async ct =>
        {
            foreach (var pre in preHandlers)
            {
                await pre.Handle(request, ct).ConfigureAwait(false);
            }

            var result = await handler.Handle(request, ct).ConfigureAwait(false);

            foreach (var post in postHandlers)
            {
                await post.Handle(request, result, ct).ConfigureAwait(false);
            }

            return result;
        };

        var behaviorList = behaviors as IReadOnlyList<IPipelineBehavior<TRequest, TResponse>>
                           ?? behaviors.ToArray();
        var behaviorOrder = BuildBehaviorOrder(behaviorList);

        var aggregate = handlerDelegate;
        for (var index = 0; index < behaviorOrder.Length; index++)
        {
            var behavior = behaviorList[behaviorOrder[index]];
            var next = aggregate;
            aggregate = ct => behavior.Handle(request, next, ct);
        }

        try
        {
            return await aggregate(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var exceptionHandlers = serviceProvider
                .GetServices<IRequestExceptionHandler<TRequest, TResponse>>()
                .ToArray();
            var exceptionHandlerOrder = BuildExceptionHandlerOrder(exceptionHandlers);
            var state = new RequestExceptionHandlerState<TResponse>();

            foreach (var index in exceptionHandlerOrder)
            {
                var exceptionHandler = exceptionHandlers[index];
                try
                {
                    await exceptionHandler.Handle(request, exception, state, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception handlerException)
                {
                    throw new AggregateException(
                        "A request exception handler failed while handling the original request exception.",
                        exception,
                        handlerException);
                }

                if (state.Handled)
                {
                    break;
                }
            }

            if (state.Handled)
            {
                return state.Response!;
            }

            throw;
        }
    }

    private static int[] BuildBehaviorOrder<TRequest, TResponse>(
        IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors)
        where TRequest : IRequest<TResponse>
    {
        return Enumerable.Range(0, behaviors.Count)
            .OrderByDescending(index => behaviors[index].Order)
            .ThenByDescending(static index => index)
            .ToArray();
    }

    private static int[] BuildExceptionHandlerOrder<TRequest, TResponse>(
        IRequestExceptionHandler<TRequest, TResponse>[] handlers)
        where TRequest : IRequest<TResponse>
    {
        return Enumerable.Range(0, handlers.Length)
            .OrderBy(index => handlers[index].Order)
            .ThenBy(static index => index)
            .ToArray();
    }
}
