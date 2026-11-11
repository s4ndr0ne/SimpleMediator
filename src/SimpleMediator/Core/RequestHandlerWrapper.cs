using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);

    public virtual ValueTask<TResponse> HandleValueTask(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => new(Handle(request, serviceProvider, cancellationToken));
}

internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly MediatorConfiguration? _configuration;
    private readonly BehaviorOrderCache _behaviorOrder = new();

    // Probe state per
    // closed (TRequest, TResponse) wrapper. Since a ServiceProvider's registrations are immutable
    // once built, the first request for a given (TRequest, TResponse) probes the container once;
    // subsequent requests skip GetServices<...>() completely when none are registered, saving
    // array allocations and dictionary lookups per Send while still honoring services added to
    // IServiceCollection AFTER AddSimpleMediator(...).
    // Handler: 0 = unknown, 1 = single Task handler, 2 = custom open handler, 3 = single ValueTask handler.
    private int _handlerResolutionState;
    private int _preHandlerState;
    private int _postHandlerState;
    private int _behaviorState;
    private int _fastPathState;

    public RequestHandlerWrapperImpl()
    {
    }

    public RequestHandlerWrapperImpl(MediatorConfiguration? configuration)
    {
        _configuration = configuration;
    }

    public override Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var configuration = _configuration ?? serviceProvider.GetService<MediatorConfiguration>();

#if !NETSTANDARD2_0
        if (configuration is { EnableTelemetry: true } && SimpleMediatorDiagnostics.HasTelemetryListeners())
        {
            return SimpleMediatorDiagnostics.TraceRequestAsync<TRequest, TResponse>(
                Volatile.Read(ref _fastPathState) == 1
                    ? HandleFastPath(typedRequest, serviceProvider, cancellationToken)
                    : HandleCore(typedRequest, serviceProvider, configuration, cancellationToken));
        }
#endif

        // Zero-state-machine, zero-array-allocation fast path once the first request has confirmed
        // that this (TRequest, TResponse) has a single closed DI handler and no pre-handlers,
        // post-handlers, or pipeline behaviors.
        if (Volatile.Read(ref _fastPathState) == 1)
        {
            return HandleFastPath(typedRequest, serviceProvider, cancellationToken);
        }

        return HandleCore(typedRequest, serviceProvider, configuration, cancellationToken);
    }

    public override ValueTask<TResponse> HandleValueTask(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var configuration = _configuration ?? serviceProvider.GetService<MediatorConfiguration>();
#if !NETSTANDARD2_0
        if (configuration is { EnableTelemetry: true } && SimpleMediatorDiagnostics.HasTelemetryListeners())
        {
            return new ValueTask<TResponse>(Handle(request, serviceProvider, cancellationToken));
        }
#endif
        if (Volatile.Read(ref _fastPathState) == 1 && Volatile.Read(ref _handlerResolutionState) == 3)
        {
            try
            {
                var vtHandler = serviceProvider.GetService<IValueTaskRequestHandler<TRequest, TResponse>>();
                if (vtHandler is not null)
                {
                    var result = vtHandler.Handle(typedRequest, cancellationToken);
                    return result.IsCompletedSuccessfully
                        ? result
                        : AwaitValueTaskAsync(typedRequest, result, serviceProvider, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RequestHandlerResolutionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new ValueTask<TResponse>(RequestHandlerPipeline.HandleExceptionAsync<TRequest, TResponse>(
                    typedRequest, exception, serviceProvider, cancellationToken));
            }
        }

        return new ValueTask<TResponse>(Handle(request, serviceProvider, cancellationToken));
    }

    private static async ValueTask<TResponse> AwaitValueTaskAsync(
        TRequest request, ValueTask<TResponse> result, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        try
        {
            return await result.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RequestHandlerPipeline.HandleExceptionAsync<TRequest, TResponse>(
                request, exception, serviceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<TResponse> HandleFastPath(
        TRequest typedRequest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = Volatile.Read(ref _handlerResolutionState) == 3
                ? RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider, _configuration, ref _handlerResolutionState).Handler
                : serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
                  ?? RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider, _configuration).Handler;
            var task = handler.Handle(typedRequest, cancellationToken)
                       ?? throw RequestHandlerPipeline.NullTaskException(handler);

            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task;
            }

            return AwaitFastPathAsync(typedRequest, task, serviceProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RequestHandlerResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RequestHandlerPipeline.HandleExceptionAsync<TRequest, TResponse>(
                typedRequest, exception, serviceProvider, cancellationToken);
        }
    }

    private static async Task<TResponse> AwaitFastPathAsync(
        TRequest typedRequest,
        Task<TResponse> task,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RequestHandlerPipeline
                .HandleExceptionAsync<TRequest, TResponse>(typedRequest, exception, serviceProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task<TResponse> HandleCore(
        TRequest typedRequest,
        IServiceProvider serviceProvider,
        MediatorConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        // The try block deliberately starts BEFORE anything is resolved from DI. A behavior, a
        // pre/post handler, or the request handler itself can fail while being *constructed*
        // (missing dependency, bad configuration, throwing constructor). Those failures are just as
        // operationally relevant as failures raised inside Handle, so IRequestExceptionHandler
        // must observe them too.
        try
        {
            var handlerLease = RequestHandlerResolver.Resolve<TRequest, TResponse>(
                serviceProvider,
                configuration,
                ref _handlerResolutionState);

            if (handlerLease.HasOwnedInstance)
            {
                return HandleOwnedLeaseAsync(typedRequest, handlerLease, serviceProvider, cancellationToken);
            }

            var preHandlers = ResolveOrSkip(serviceProvider, ref _preHandlerState, static sp => sp.GetServices<IPreRequestHandler<TRequest, TResponse>>());
            var postHandlers = ResolveOrSkip(serviceProvider, ref _postHandlerState, static sp => sp.GetServices<IPostRequestHandler<TRequest, TResponse>>());

            // If exactly one behavior is registered, resolve it directly via GetService<T>() to avoid
            // the per-request T[1] array allocation of GetServices<T>() and bypass permutation ordering.
            if (Volatile.Read(ref _behaviorState) == 2)
            {
                var singleBehavior = serviceProvider.GetService<IPipelineBehavior<TRequest, TResponse>>();
                if (singleBehavior is not null)
                {
                    var singleTask = RequestHandlerPipeline.ExecuteWithSingleBehavior(
                        typedRequest,
                        handlerLease.Handler,
                        preHandlers,
                        postHandlers,
                        singleBehavior,
                        cancellationToken);

                    if (singleTask.Status == TaskStatus.RanToCompletion)
                    {
                        return singleTask;
                    }

                    return AwaitFastPathAsync(typedRequest, singleTask, serviceProvider, cancellationToken);
                }
            }

            var behaviors = ResolveOrSkip(serviceProvider, ref _behaviorState, static sp => sp.GetServices<IPipelineBehavior<TRequest, TResponse>>());

            if (Volatile.Read(ref _fastPathState) == 0 &&
                Volatile.Read(ref _handlerResolutionState) != 0 &&
                Volatile.Read(ref _preHandlerState) != 0 &&
                Volatile.Read(ref _postHandlerState) != 0 &&
                Volatile.Read(ref _behaviorState) != 0)
            {
                var canFastPath =
                    Volatile.Read(ref _handlerResolutionState) is 1 or 3 &&
                    Volatile.Read(ref _preHandlerState) == 1 &&
                    Volatile.Read(ref _postHandlerState) == 1 &&
                    Volatile.Read(ref _behaviorState) == 1;
                Volatile.Write(ref _fastPathState, canFastPath ? 1 : 2);
            }

            var task = RequestHandlerPipeline.Execute(
                typedRequest,
                handlerLease.Handler,
                preHandlers,
                postHandlers,
                behaviors,
                _behaviorOrder,
                cancellationToken);

            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task;
            }

            return AwaitFastPathAsync(typedRequest, task, serviceProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is control flow, never an error: it is deliberately not offered to
            // IRequestExceptionHandler, whether it came from the request's own token or from a
            // linked token a behavior observed.
            throw;
        }
        catch (RequestHandlerResolutionException)
        {
            // Handler selection is a wiring defect (no handler / more than one handler), not a
            // failure of the request. Letting an exception handler observe it would hide the
            // misconfiguration behind a substitute response.
            throw;
        }
        catch (Exception exception)
        {
            return RequestHandlerPipeline.HandleExceptionAsync<TRequest, TResponse>(
                typedRequest, exception, serviceProvider, cancellationToken);
        }
    }

    private async Task<TResponse> HandleOwnedLeaseAsync(
        TRequest typedRequest,
        RequestHandlerResolver.HandlerLease<TRequest, TResponse> handlerLease,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var preHandlers = ResolveOrSkip(serviceProvider, ref _preHandlerState, static sp => sp.GetServices<IPreRequestHandler<TRequest, TResponse>>());
            var postHandlers = ResolveOrSkip(serviceProvider, ref _postHandlerState, static sp => sp.GetServices<IPostRequestHandler<TRequest, TResponse>>());

            // If exactly one behavior is registered, resolve it directly via GetService<T>() to avoid
            // the per-request T[1] array allocation of GetServices<T>() and bypass permutation ordering.
            if (Volatile.Read(ref _behaviorState) == 2)
            {
                var singleBehavior = serviceProvider.GetService<IPipelineBehavior<TRequest, TResponse>>();
                if (singleBehavior is not null)
                {
                    return await RequestHandlerPipeline.ExecuteWithSingleBehavior(
                        typedRequest,
                        handlerLease.Handler,
                        preHandlers,
                        postHandlers,
                        singleBehavior,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var behaviors = ResolveOrSkip(serviceProvider, ref _behaviorState, static sp => sp.GetServices<IPipelineBehavior<TRequest, TResponse>>());

            if (Volatile.Read(ref _fastPathState) == 0)
            {
                Volatile.Write(ref _fastPathState, 2);
            }

            return await RequestHandlerPipeline.Execute(
                typedRequest,
                handlerLease.Handler,
                preHandlers,
                postHandlers,
                behaviors,
                _behaviorOrder,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RequestHandlerPipeline
                .HandleExceptionAsync<TRequest, TResponse>(typedRequest, exception, serviceProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Closed and native open-generic handlers are owned and tracked by Microsoft DI.
            // Custom-mapped open-generic transient handlers are activated manually and are owned by
            // this request, so the lease disposes exactly those.
            await handlerLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<TService> ResolveOrSkip<TService>(
        IServiceProvider serviceProvider,
        ref int state,
        Func<IServiceProvider, IEnumerable<TService>> resolver)
    {
        var currentState = Volatile.Read(ref state);
        if (currentState == 1)
        {
            return Array.Empty<TService>();
        }

        var resolved = resolver(serviceProvider);
        var list = resolved as IReadOnlyList<TService> ?? resolved.ToArray();
        if (currentState == 0)
        {
            var nextState = list.Count switch
            {
                0 => 1,
                1 => 2,
                _ => 3
            };
            Volatile.Write(ref state, nextState);
        }

        return list;
    }
}
