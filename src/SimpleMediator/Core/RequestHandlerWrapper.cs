using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class RequestHandlerWrapper<TResponse>
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly MediatorConfiguration? _configuration;
    private readonly BehaviorOrderCache _behaviorOrder = new();

    // Tri-state probes (0 = unknown, 1 = none registered, 2 = at least one registered) per
    // closed (TRequest, TResponse) wrapper. Since a ServiceProvider's registrations are immutable
    // once built, the first request for a given (TRequest, TResponse) probes the container once;
    // subsequent requests skip GetServices<...>() completely when none are registered, saving 3
    // array allocations and 3 dictionary lookups per Send while still honoring services added to
    // IServiceCollection AFTER AddSimpleMediator(...).
    private int _preHandlerState;
    private int _postHandlerState;
    private int _behaviorState;

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
                HandleCoreAsync(typedRequest, serviceProvider, configuration, cancellationToken));
        }
#endif

        // Zero-state-machine, zero-array-allocation fast path once the first request has confirmed
        // that this (TRequest, TResponse) has no pre-handlers, post-handlers, behaviors, or custom
        // open-generic handlers.
        if (Volatile.Read(ref _preHandlerState) == 1 &&
            Volatile.Read(ref _postHandlerState) == 1 &&
            Volatile.Read(ref _behaviorState) == 1 &&
            configuration is { CustomOpenGenericRequestHandlers.Count: 0 })
        {
            return HandleFastPath(typedRequest, serviceProvider, cancellationToken);
        }

        return HandleCoreAsync(typedRequest, serviceProvider, configuration, cancellationToken);
    }

    private Task<TResponse> HandleFastPath(
        TRequest typedRequest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var handlerLease = RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider, _configuration);
            var task = handlerLease.Handler.Handle(typedRequest, cancellationToken)
                       ?? throw RequestHandlerPipeline.NullTaskException(handlerLease.Handler);

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

    private async Task<TResponse> HandleCoreAsync(
        TRequest typedRequest,
        IServiceProvider serviceProvider,
        MediatorConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var handlerLease = default(RequestHandlerResolver.HandlerLease<TRequest, TResponse>);

        // The try block deliberately starts BEFORE anything is resolved from DI. A behavior, a
        // pre/post handler, or the request handler itself can fail while being *constructed*
        // (missing dependency, bad configuration, throwing constructor). Those failures are just as
        // operationally relevant as failures raised inside Handle, so IRequestExceptionHandler
        // must observe them too.
        try
        {
            handlerLease = RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider, configuration);

            var preHandlers = ResolveOrSkip(serviceProvider, ref _preHandlerState, static sp => sp.GetServices<IPreRequestHandler<TRequest, TResponse>>());
            var postHandlers = ResolveOrSkip(serviceProvider, ref _postHandlerState, static sp => sp.GetServices<IPostRequestHandler<TRequest, TResponse>>());
            var behaviors = ResolveOrSkip(serviceProvider, ref _behaviorState, static sp => sp.GetServices<IPipelineBehavior<TRequest, TResponse>>());

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
        if (Volatile.Read(ref state) == 1)
        {
            return Array.Empty<TService>();
        }

        var resolved = resolver(serviceProvider);
        var list = resolved as IReadOnlyList<TService> ?? resolved.ToArray();
        if (Volatile.Read(ref state) == 0)
        {
            Volatile.Write(ref state, list.Count == 0 ? 1 : 2);
        }

        return list;
    }
}
