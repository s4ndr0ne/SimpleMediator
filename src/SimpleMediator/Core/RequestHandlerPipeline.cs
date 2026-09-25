using System.Runtime.ExceptionServices;
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
        var behaviorList = behaviors as IReadOnlyList<IPipelineBehavior<TRequest, TResponse>>
                           ?? behaviors.ToArray();
        var preList = preHandlers as IReadOnlyList<IPreRequestHandler<TRequest, TResponse>>
                      ?? preHandlers.ToArray();
        var postList = postHandlers as IReadOnlyList<IPostRequestHandler<TRequest, TResponse>>
                       ?? postHandlers.ToArray();

        try
        {
            if (behaviorList.Count == 0)
            {
                if (preList.Count == 0 && postList.Count == 0)
                {
                    return await handler.Handle(request, cancellationToken).ConfigureAwait(false);
                }

                foreach (var pre in preList)
                {
                    await pre.Handle(request, cancellationToken).ConfigureAwait(false);
                }

                var result = await handler.Handle(request, cancellationToken).ConfigureAwait(false);

                foreach (var post in postList)
                {
                    await post.Handle(request, result, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }

            RequestHandlerDelegate<TResponse> handlerDelegate = async ct =>
            {
                foreach (var pre in preList)
                {
                    await pre.Handle(request, ct).ConfigureAwait(false);
                }

                var result = await handler.Handle(request, ct).ConfigureAwait(false);

                foreach (var post in postList)
                {
                    await post.Handle(request, result, ct).ConfigureAwait(false);
                }

                return result;
            };

            var behaviorOrder = BuildBehaviorOrder(behaviorList);

            var aggregate = handlerDelegate;
            for (var index = 0; index < behaviorOrder.Length; index++)
            {
                var behavior = behaviorList[behaviorOrder[index]];
                var next = aggregate;
                aggregate = ct => behavior.Handle(request, next, ct);
            }

            return await aggregate(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await HandleExceptionAsync<TRequest, TResponse>(request, exception, serviceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TResponse> HandleExceptionAsync<TRequest, TResponse>(
        TRequest request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var exceptionHandlers = serviceProvider
            .GetServices<IRequestExceptionHandler<TRequest, TResponse>>()
            .ToArray();

        if (exceptionHandlers.Length == 0)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

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

        ExceptionDispatchInfo.Capture(exception).Throw();
        return default!;
    }

    private static int[] BuildBehaviorOrder<TRequest, TResponse>(
        IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors)
        where TRequest : IRequest<TResponse>
    {
        var count = behaviors.Count;
        if (count == 1)
        {
            return [0];
        }

        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var cmp = behaviors[b].Order.CompareTo(behaviors[a].Order);
            // The wrapping loop consumes this list from left to right. Reverse the
            // tie order while building the chain so the first registered behavior
            // becomes the outermost one and runs first.
            return cmp != 0 ? cmp : b.CompareTo(a);
        });

        return order;
    }

    private static int[] BuildExceptionHandlerOrder<TRequest, TResponse>(
        IRequestExceptionHandler<TRequest, TResponse>[] handlers)
        where TRequest : IRequest<TResponse>
    {
        var count = handlers.Length;
        if (count == 1)
        {
            return [0];
        }

        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var cmp = handlers[a].Order.CompareTo(handlers[b].Order);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        return order;
    }
}
