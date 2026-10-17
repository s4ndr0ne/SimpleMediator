using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Configuration;
using SimpleMediator.Infrastructure;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Core;

internal abstract class StreamRequestHandlerWrapper<TResponse>
{
    public abstract IAsyncEnumerable<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal sealed class StreamRequestHandlerWrapperImpl<TRequest, TResponse> : StreamRequestHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    private readonly MediatorConfiguration? _configuration;
    private readonly BehaviorOrderCache _behaviorOrder = new();
    private volatile int _behaviorState;

    public StreamRequestHandlerWrapperImpl()
    {
    }

    public StreamRequestHandlerWrapperImpl(MediatorConfiguration? configuration)
    {
        _configuration = configuration;
    }

    public override IAsyncEnumerable<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var configuration = _configuration ?? serviceProvider.GetService<MediatorConfiguration>();
        var handlerLease = StreamRequestHandlerResolver.Resolve<TRequest, TResponse>(serviceProvider, configuration);

        IReadOnlyList<IStreamPipelineBehavior<TRequest, TResponse>> behaviorList = Array.Empty<IStreamPipelineBehavior<TRequest, TResponse>>();
        if (_behaviorState != 1)
        {
            var behaviors = serviceProvider.GetServices<IStreamPipelineBehavior<TRequest, TResponse>>();
            behaviorList = behaviors as IReadOnlyList<IStreamPipelineBehavior<TRequest, TResponse>>
                           ?? behaviors.ToArray();
            if (_behaviorState == 0)
            {
                _behaviorState = behaviorList.Count == 0 ? 1 : 2;
            }
        }

        StreamHandlerDelegate<TResponse> handlerDelegate = () =>
            handlerLease.Handler.Handle(typedRequest, cancellationToken)
            ?? throw NullStreamException(handlerLease.Handler);

        var aggregate = handlerDelegate;
        if (behaviorList.Count > 0)
        {
            var order = _behaviorOrder.GetStreamOrder(behaviorList);
            for (var index = 0; index < order.Length; index++)
            {
                var behavior = behaviorList[order[index]];
                var next = aggregate;
                aggregate = () => behavior.Handle(typedRequest, next, cancellationToken)
                                  ?? throw NullStreamException(behavior);
            }
        }

        if (!handlerLease.HasOwnedInstance)
        {
            return aggregate();
        }

        return IterateWithLeaseDisposal(aggregate, handlerLease, cancellationToken);
    }

    private static async IAsyncEnumerable<TResponse> IterateWithLeaseDisposal(
        StreamHandlerDelegate<TResponse> pipeline,
        StreamRequestHandlerResolver.StreamHandlerLease<TRequest, TResponse> lease,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            var stream = pipeline();
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static InvalidOperationException NullStreamException(object source)
        => new($"'{source.GetType().FullName}' returned a null IAsyncEnumerable. Every stream handler and behavior must return a non-null stream.");
}

internal static class StreamRequestHandlerResolver
{
    public static StreamHandlerLease<TRequest, TResponse> Resolve<TRequest, TResponse>(
        IServiceProvider serviceProvider,
        MediatorConfiguration? configuration)
        where TRequest : IStreamRequest<TResponse>
    {
        IStreamRequestHandler<TRequest, TResponse>? closedHandler = null;
        var closedHandlerCount = 0;
        var candidates = serviceProvider.GetServices<IStreamRequestHandler<TRequest, TResponse>>();
        if (candidates is IReadOnlyList<IStreamRequestHandler<TRequest, TResponse>> candidateList)
        {
            closedHandlerCount = candidateList.Count;
            if (closedHandlerCount > 0)
            {
                closedHandler = candidateList[0];
            }
        }
        else
        {
            foreach (var candidate in candidates)
            {
                closedHandlerCount++;
                if (closedHandlerCount == 1)
                {
                    closedHandler = candidate;
                }
            }
        }

        if (closedHandlerCount == 0)
        {
            throw new RequestHandlerResolutionException(
                $"No stream request handler registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'.");
        }

        if (closedHandlerCount > 1)
        {
            throw new RequestHandlerResolutionException(
                $"Multiple stream request handlers registered for '{typeof(TRequest).FullName}' with response '{typeof(TResponse).FullName}'. " +
                "A stream request can only have one handler.");
        }

        return new StreamHandlerLease<TRequest, TResponse>(closedHandler!);
    }

    internal readonly struct StreamHandlerLease<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        private readonly object? _ownedInstance;

        public StreamHandlerLease(IStreamRequestHandler<TRequest, TResponse> handler, object? ownedInstance = null)
        {
            Handler = handler;
            _ownedInstance = ownedInstance;
        }

        public IStreamRequestHandler<TRequest, TResponse> Handler { get; }

        public bool HasOwnedInstance => _ownedInstance is not null;

        public ValueTask DisposeAsync()
        {
            switch (_ownedInstance)
            {
                case IAsyncDisposable asyncDisposable:
                    return asyncDisposable.DisposeAsync();
                case IDisposable disposable:
                    disposable.Dispose();
                    return default;
                default:
                    return default;
            }
        }
    }
}
