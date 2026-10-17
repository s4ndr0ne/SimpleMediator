using Microsoft.Extensions.DependencyInjection;
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
    // The wrapper itself is cached per (request, response) pair on the container's configuration
    // singleton, so this cache shares exactly the same lifetime and is never cross-container
    // static state (the same reasoning as WrapperCache). It lets a dispatch whose behavior types
    // and Order values are unchanged reuse the sorted order instead of re-allocating and
    // re-sorting it every request; see BehaviorOrderCache for the validation contract.
    private readonly BehaviorOrderCache _behaviorOrder = new();

    public override async Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handlerLease = default(RequestHandlerResolver.HandlerLease<TRequest, TResponse>);

        // The try block deliberately starts BEFORE anything is resolved from DI. A behavior, a
        // pre/post handler, or the request handler itself can fail while being *constructed*
        // (missing dependency, bad configuration, throwing constructor). Those failures are just as
        // operationally relevant as failures raised inside Handle, so IRequestExceptionHandler
        // must observe them too.
        try
        {
            handlerLease = RequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider);

            var preHandlers = serviceProvider.GetServices<IPreRequestHandler<TRequest, TResponse>>();
            var postHandlers = serviceProvider.GetServices<IPostRequestHandler<TRequest, TResponse>>();
            var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();

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
}
