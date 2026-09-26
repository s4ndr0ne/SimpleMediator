using SimpleMediator.Interfaces;

namespace SimpleMediator.SourceGenerator.Tests.Fixtures;

/// <summary>Records the order in which handlers, processors and behaviors run.</summary>
public sealed class Trace
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler(Trace trace) : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        trace.Add("handler:Ping");
        return Task.FromResult("Pong " + request.Message);
    }
}

public sealed record Count(int Value) : IRequest<int>;

public sealed class CountHandler(Trace trace) : IRequestHandler<Count, int>
{
    public Task<int> Handle(Count request, CancellationToken cancellationToken)
    {
        trace.Add("handler:Count");
        return Task.FromResult(request.Value + 1);
    }
}

public sealed record Fire(string Target) : IRequest;

public sealed class FireHandler(Trace trace) : IRequestHandler<Fire, Unit>
{
    public Task<Unit> Handle(Fire request, CancellationToken cancellationToken)
    {
        trace.Add("handler:Fire:" + request.Target);
        return Unit.Task;
    }
}

/// <summary>Custom open generic: the request type is itself generic, closed per Send call site.</summary>
public sealed record Echo<T>(T Value) : IRequest<T>;

public sealed class EchoHandler<T>(Trace trace) : IRequestHandler<Echo<T>, T>
{
    public Task<T> Handle(Echo<T> request, CancellationToken cancellationToken)
    {
        trace.Add("handler:Echo<" + typeof(T).Name + ">");
        return Task.FromResult(request.Value);
    }
}

public sealed record Explode(string Reason) : IRequest<string>;

public sealed class ExplodeHandler : IRequestHandler<Explode, string>
{
    public Task<string> Handle(Explode request, CancellationToken cancellationToken)
        => throw new InvalidOperationException(request.Reason);
}

public sealed class ExplodeRecovery(Trace trace) : IRequestExceptionHandler<Explode, string>
{
    public Task Handle(Explode request, Exception exception, RequestExceptionHandlerState<string> state, CancellationToken cancellationToken)
    {
        trace.Add("recovered:" + exception.Message);
        state.SetHandled("recovered");
        return Task.CompletedTask;
    }
}

/// <summary>Open-generic pre-processor: closed by the generator for every known request.</summary>
public sealed class AuditPreProcessor<TRequest, TResponse>(Trace trace) : IPreRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task Handle(TRequest request, CancellationToken cancellationToken)
    {
        trace.Add("pre:" + typeof(TRequest).Name);
        return Task.CompletedTask;
    }
}

public sealed class PingPostProcessor(Trace trace) : IPostRequestHandler<Ping, string>
{
    public Task Handle(Ping request, string response, CancellationToken cancellationToken)
    {
        trace.Add("post:Ping:" + response);
        return Task.CompletedTask;
    }
}

public sealed class OuterBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public int Order => 0;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("outer:before");
        var response = await next(cancellationToken);
        trace.Add("outer:after");
        return response;
    }
}

public sealed class InnerBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public int Order => 0;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("inner:before");
        var response = await next(cancellationToken);
        trace.Add("inner:after");
        return response;
    }
}

/// <summary>Runs first despite being registered last, because of its lower Order.</summary>
public sealed class PingOnlyBehavior(Trace trace) : IPipelineBehavior<Ping, string>
{
    public int Order => -1;

    public async Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        trace.Add("ping-only:before");
        var response = await next(cancellationToken);
        trace.Add("ping-only:after");
        return response;
    }
}

public interface ICommandMarker;

public sealed record Launch(string Name) : IRequest<string>, ICommandMarker;

public sealed class LaunchHandler : IRequestHandler<Launch, string>
{
    public Task<string> Handle(Launch request, CancellationToken cancellationToken) => Task.FromResult("launched " + request.Name);
}

/// <summary>Constrained open behavior: only closed for requests implementing <see cref="ICommandMarker"/>.</summary>
public sealed class CommandOnlyBehavior<TRequest, TResponse>(Trace trace) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, ICommandMarker
{
    public int Order => 0;

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Add("command-only:" + typeof(TRequest).Name);
        return next(cancellationToken);
    }
}

public sealed record Notice(string Text) : INotification;

public sealed class NoticeHandlerA(Trace trace) : INotificationHandler<Notice>
{
    public Task Handle(Notice notification, CancellationToken cancellationToken)
    {
        trace.Add("notice:A");
        return Task.CompletedTask;
    }
}

public sealed class NoticeHandlerB(Trace trace) : INotificationHandler<Notice>
{
    public Task Handle(Notice notification, CancellationToken cancellationToken)
    {
        trace.Add("notice:B");
        return Task.CompletedTask;
    }
}

/// <summary>Open-generic notification handler: closed for every known notification.</summary>
public sealed class NotificationAuditor<TNotification>(Trace trace) : INotificationHandler<TNotification>
    where TNotification : INotification
{
    public Task Handle(TNotification notification, CancellationToken cancellationToken)
    {
        trace.Add("audit:" + typeof(TNotification).Name);
        return Task.CompletedTask;
    }
}

public sealed record Hidden(string Value) : IRequest<string>;

/// <summary>Excluded by an assembly filter in the filter tests.</summary>
public sealed class HiddenHandler : IRequestHandler<Hidden, string>
{
    public Task<string> Handle(Hidden request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
}
