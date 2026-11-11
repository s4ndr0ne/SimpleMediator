using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal static class RequestHandlerPipeline
{
    /// <summary>
    /// Runs the pre-handlers, the request handler, and the post-handlers, wrapped in the
    /// registered pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// This method does <em>not</em> catch exceptions, and its fast paths add no async state
    /// machine of their own: when there is nothing to sequence, the handler's own task is handed
    /// back to the caller untouched, and a missing task throws synchronously. The caller
    /// (<see cref="RequestHandlerWrapperImpl{TRequest, TResponse}"/>) owns the single try/catch
    /// either way, so failures raised while <em>constructing</em> the handler, the pre/post
    /// handlers, or the behaviors — and failures raised before the first await — are routed to
    /// <see cref="IRequestExceptionHandler{TRequest, TResponse}"/> identically.
    /// </remarks>
    public static Task<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IEnumerable<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IEnumerable<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors,
        BehaviorOrderCache behaviorOrderCache,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        // Avoid materializing the DI result with ToList(): Microsoft DI hands back arrays, which
        // are already IReadOnlyList, so the cast costs nothing.
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
                // Nothing to sequence: return the handler's own task so this dispatch adds no
                // Task and no async state machine. A null task throws synchronously; the
                // wrapper's try block routes it to the exception handlers exactly like a
                // faulted one, because a synchronous throw inside its try block is caught there.
                return handler.Handle(request, cancellationToken)
                       ?? throw NullTaskException(handler);
            }

            return ExecuteWithoutBehaviors(request, handler, preList, postList, cancellationToken);
        }

        if (behaviorList.Count == 1)
        {
            return ExecuteWithSingleBehavior(
                request,
                handler,
                preList,
                postList,
                behaviorList[0],
                cancellationToken);
        }

        return ExecuteWithBehaviors(
            request,
            handler,
            preList,
            postList,
            behaviorList,
            behaviorOrderCache.GetOrder(behaviorList),
            cancellationToken);
    }

    /// <summary>
    /// Fast-path execution for pipelines with exactly one behavior, avoiding index permutations,
    /// sorting cache reads, and multiple step delegate allocations.
    /// </summary>
    public static Task<TResponse> ExecuteWithSingleBehavior<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IReadOnlyList<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IReadOnlyList<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        IPipelineBehavior<TRequest, TResponse> behavior,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        RequestHandlerDelegate<TResponse> next = preHandlers.Count == 0 && postHandlers.Count == 0
            ? new TerminalHandlerStep<TRequest, TResponse>(request, handler).Invoke
            : new PrePostHandlerStep<TRequest, TResponse>(request, handler, preHandlers, postHandlers).Invoke;

        return behavior.Handle(request, next, cancellationToken)
               ?? throw NullTaskException(behavior);
    }

    private static async Task<TResponse> ExecuteWithoutBehaviors<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IReadOnlyList<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IReadOnlyList<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        for (var i = 0; i < preHandlers.Count; i++)
        {
            var pre = preHandlers[i];
            var preTask = pre.Handle(request, cancellationToken);
            if (preTask is null)
            {
                throw NullTaskException(pre);
            }

            await preTask.ConfigureAwait(false);
        }

        var handlerTask = handler.Handle(request, cancellationToken);
        if (handlerTask is null)
        {
            throw NullTaskException(handler);
        }

        var result = await handlerTask.ConfigureAwait(false);

        for (var i = 0; i < postHandlers.Count; i++)
        {
            var post = postHandlers[i];
            var postTask = post.Handle(request, result, cancellationToken);
            if (postTask is null)
            {
                throw NullTaskException(post);
            }

            await postTask.ConfigureAwait(false);
        }

        return result;
    }

    private static Task<TResponse> ExecuteWithBehaviors<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IReadOnlyList<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IReadOnlyList<IPostRequestHandler<TRequest, TResponse>> postHandlers,
        IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors,
        int[] behaviorOrder,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        // When there are no pre- or post-handlers (the common case), use a dedicated 2-field step
        // instead of a shared compiler DisplayClass that also captures preHandlers and postHandlers.
        RequestHandlerDelegate<TResponse> aggregate = preHandlers.Count == 0 && postHandlers.Count == 0
            ? new TerminalHandlerStep<TRequest, TResponse>(request, handler).Invoke
            : new PrePostHandlerStep<TRequest, TResponse>(request, handler, preHandlers, postHandlers).Invoke;

        // Wrap all but the outermost behavior in self-contained step delegates; invoke the outermost
        // behavior directly to save one step and one RequestHandlerDelegate allocation per request.
        var lastIndex = behaviorOrder.Length - 1;
        for (var index = 0; index < lastIndex; index++)
        {
            var behavior = behaviors[behaviorOrder[index]];
            aggregate = new BehaviorPipelineStep<TRequest, TResponse>(request, behavior, aggregate).Invoke;
        }

        var outermost = behaviors[behaviorOrder[lastIndex]];
        return outermost.Handle(request, aggregate, cancellationToken) ?? throw NullTaskException(outermost);
    }

    private sealed class TerminalHandlerStep<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler)
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Invoke(CancellationToken ct)
            => handler.Handle(request, ct) ?? throw NullTaskException(handler);
    }

    private sealed class PrePostHandlerStep<TRequest, TResponse>(
        TRequest request,
        IRequestHandler<TRequest, TResponse> handler,
        IReadOnlyList<IPreRequestHandler<TRequest, TResponse>> preHandlers,
        IReadOnlyList<IPostRequestHandler<TRequest, TResponse>> postHandlers)
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Invoke(CancellationToken ct)
            => ExecuteWithoutBehaviors(request, handler, preHandlers, postHandlers, ct);
    }

    private sealed class BehaviorPipelineStep<TRequest, TResponse>(
        TRequest request,
        IPipelineBehavior<TRequest, TResponse> behavior,
        RequestHandlerDelegate<TResponse> next)
        where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Invoke(CancellationToken ct)
            => behavior.Handle(request, next, ct) ?? throw NullTaskException(behavior);
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
        IReadOnlyList<IRequestExceptionHandler<TRequest, TResponse>> exceptionHandlers;
        try
        {
            var resolved = serviceProvider
                .GetServices<IRequestExceptionHandler<TRequest, TResponse>>();
            // Microsoft DI materializes an array, which is already IReadOnlyList; only copy when
            // some other provider hands back a lazily evaluated sequence.
            exceptionHandlers = resolved as IReadOnlyList<IRequestExceptionHandler<TRequest, TResponse>>
                                ?? resolved.ToArray();
        }
        catch (Exception)
        {
            // The exception handlers themselves could not be constructed. That is a wiring defect,
            // and surfacing it would replace the real request failure — the thing the caller
            // actually needs to see — with an unrelated DI error. Preserve the original exception.
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }

        if (exceptionHandlers.Count == 0)
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
                var handlerTask = exceptionHandler.Handle(request, exception, state, cancellationToken);
                if (handlerTask is null)
                {
                    throw NullTaskException(exceptionHandler);
                }

                await handlerTask.ConfigureAwait(false);
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

    internal static InvalidOperationException NullTaskException(object source)
        => new($"'{source.GetType().FullName}' returned a null Task. Every mediator handler method must return a non-null Task.");

    private static int[] BuildExceptionHandlerOrder<TRequest, TResponse>(
        IReadOnlyList<IRequestExceptionHandler<TRequest, TResponse>> handlers)
        where TRequest : IRequest<TResponse>
    {
        var count = handlers.Count;
        if (count == 1)
        {
            return [0];
        }

        // Snapshot Order exactly once per resolution: reading it inside the comparison would call
        // the property O(n log n) times and, if the value is not stable, could make Array.Sort see
        // an inconsistent ordering.
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
