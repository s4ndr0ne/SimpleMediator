using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal static class RequestHandlerPipeline
{
    /// <summary>
    /// Runs the pre-handlers, the request handler, and the post-handlers, wrapped in the
    /// registered pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// This method does <em>not</em> catch exceptions. The caller
    /// (<see cref="RequestHandlerWrapperImpl{TRequest, TResponse}"/>) owns the single try/catch so
    /// that failures raised while <em>constructing</em> the handler, the pre/post handlers, or the
    /// behaviors are routed to <see cref="IRequestExceptionHandler{TRequest, TResponse}"/> as well.
    /// </remarks>
    public static async Task<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IEnumerable<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IEnumerable<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var behaviorList = behaviors as IReadOnlyList<IPipelineBehavior<TRequest, TResponse>>
                           ?? behaviors.ToArray();
        var preList = preHandlers as IReadOnlyList<IPreRequestHandler<TRequest, TResponse>>
                      ?? preHandlers.ToArray();
        var postList = postHandlers as IReadOnlyList<IPostRequestHandler<TRequest, TResponse>>
                       ?? postHandlers.ToArray();

        if (behaviorList.Count == 0)
        {
            if (preList.Count == 0 && postList.Count == 0)
            {
                return await Await(handler.Handle(request, cancellationToken), handler).ConfigureAwait(false);
            }

            foreach (var pre in preList)
            {
                await Await(pre.Handle(request, cancellationToken), pre).ConfigureAwait(false);
            }

            var directResult = await Await(handler.Handle(request, cancellationToken), handler).ConfigureAwait(false);

            foreach (var post in postList)
            {
                await Await(post.Handle(request, directResult, cancellationToken), post).ConfigureAwait(false);
            }

            return directResult;
        }

        RequestHandlerDelegate<TResponse> handlerDelegate = async ct =>
        {
            foreach (var pre in preList)
            {
                await Await(pre.Handle(request, ct), pre).ConfigureAwait(false);
            }

            var result = await Await(handler.Handle(request, ct), handler).ConfigureAwait(false);

            foreach (var post in postList)
            {
                await Await(post.Handle(request, result, ct), post).ConfigureAwait(false);
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

        return await Await(aggregate(cancellationToken), behaviorList[behaviorOrder[0]]).ConfigureAwait(false);
    }

    /// <summary>
    /// Offers <paramref name="exception"/> to the registered
    /// <see cref="IRequestExceptionHandler{TRequest, TResponse}"/> instances and returns the
    /// substituted response, or rethrows the original exception with its stack trace intact.
    /// </summary>
    public static async Task<TResponse> HandleExceptionAsync<TRequest, TResponse>(
        TRequest request,
        Exception exception,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        IRequestExceptionHandler<TRequest, TResponse>[] exceptionHandlers;
        try
        {
            exceptionHandlers = serviceProvider
                .GetServices<IRequestExceptionHandler<TRequest, TResponse>>()
                .ToArray();
        }
        catch (Exception)
        {
            // The exception handlers themselves could not be constructed. That is a wiring defect,
            // and surfacing it would replace the real request failure — the thing the caller
            // actually needs to see — with an unrelated DI error. Preserve the original exception.
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

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
                await Await(exceptionHandler.Handle(request, exception, state, cancellationToken), exceptionHandler)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
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

    /// <summary>
    /// Awaits a user-supplied <see cref="Task"/> and turns the "handler forgot to return a Task"
    /// mistake into an actionable error instead of a bare <see cref="NullReferenceException"/>.
    /// </summary>
    private static async Task Await(Task? task, object source)
    {
        if (task is null)
        {
            throw new InvalidOperationException(
                $"'{source.GetType().FullName}' returned a null Task. Every mediator handler method must return a non-null Task.");
        }

        await task.ConfigureAwait(false);
    }

    private static async Task<T> Await<T>(Task<T>? task, object source)
    {
        if (task is null)
        {
            throw new InvalidOperationException(
                $"'{source.GetType().FullName}' returned a null Task. Every mediator handler method must return a non-null Task.");
        }

        return await task.ConfigureAwait(false);
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

        // Snapshot Order exactly once per request. Reading it inside the comparison would call the
        // property O(n log n) times and, if the value is not stable, could make Array.Sort see an
        // inconsistent ordering.
        var orders = new int[count];
        for (var i = 0; i < count; i++)
        {
            orders[i] = behaviors[i].Order;
        }

        var order = IdentityOrder(count);

        Array.Sort(order, (a, b) =>
        {
            var cmp = orders[b].CompareTo(orders[a]);
            // v4 contract: for equal Order the first registered behavior becomes the
            // outermost one and runs first (FIFO, like MediatR / ASP.NET Core middleware).
            // The wrapping loop consumes this list left to right, so reverse the tie
            // order here to achieve FIFO execution.
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

        // Snapshot once per resolution for the same reason as the behavior order above.
        var orders = new int[count];
        for (var i = 0; i < count; i++)
        {
            orders[i] = handlers[i].Order;
        }

        var order = IdentityOrder(count);

        Array.Sort(order, (a, b) =>
        {
            var cmp = orders[a].CompareTo(orders[b]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        return order;
    }

    private static int[] IdentityOrder(int count)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        return order;
    }
}
