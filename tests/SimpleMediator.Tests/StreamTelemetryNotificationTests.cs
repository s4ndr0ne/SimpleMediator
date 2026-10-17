using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SimpleMediator.Core;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Tests;

public sealed class StreamTelemetryNotificationTests
{
    public sealed record CountStreamRequest(int Count) : IStreamRequest<int>;

    public sealed class CountStreamHandler : IStreamRequestHandler<CountStreamRequest, int>
    {
        public async IAsyncEnumerable<int> Handle(
            CountStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 1; i <= request.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i;
            }
        }
    }

    public sealed class DoublingStreamBehavior : IStreamPipelineBehavior<CountStreamRequest, int>
    {
        public int Order => 1;

        public async IAsyncEnumerable<int> Handle(
            CountStreamRequest request,
            StreamHandlerDelegate<int> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return item * 2;
            }
        }
    }

    public sealed class OffsetStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
        where TRequest : IStreamRequest<TResponse>
    {
        public int Order => 2;

        public async IAsyncEnumerable<TResponse> Handle(
            TRequest request,
            StreamHandlerDelegate<TResponse> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item is int i && (object)(i + 10) is TResponse mapped)
                {
                    yield return mapped;
                }
                else
                {
                    yield return item;
                }
            }
        }
    }

    [Fact]
    public async Task CreateStream_dispatches_through_stream_behaviors_in_order()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(StreamTelemetryNotificationTests).Assembly,
                type => type == typeof(CountStreamHandler));
            options.AddStreamBehavior(typeof(DoublingStreamBehavior));
            options.AddStreamBehavior(typeof(OffsetStreamBehavior<,>));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var items = new List<int>();
        await foreach (var item in sender.CreateStream(new CountStreamRequest(3)))
        {
            items.Add(item);
        }

        // Order 1 (Doubling) wraps Order 2 (Offset +10):
        // Handler yields 1, 2, 3 -> Offset yields 11, 12, 13 -> Doubling yields 22, 24, 26
        Assert.Equal([22, 24, 26], items);
    }

    [Fact]
    public void CreateStream_without_handler_throws_RequestHandlerResolutionException()
    {
        var services = new ServiceCollection();
        services.AddSimpleMediator();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Throws<RequestHandlerResolutionException>(
            () => sender.CreateStream(new CountStreamRequest(1)));
    }

    public sealed record AuditedNotification(string Message) : INotification;

    public sealed class NotificationLog
    {
        public List<string> Entries { get; } = [];
    }

    public sealed class AuditedNotificationHandler(NotificationLog log) : INotificationHandler<AuditedNotification>
    {
        public Task Handle(AuditedNotification notification, CancellationToken cancellationToken)
        {
            log.Entries.Add("handler:" + notification.Message);
            return Task.CompletedTask;
        }
    }

    public sealed class OuterNotificationBehavior<TNotification>(NotificationLog log) : INotificationBehavior<TNotification>
        where TNotification : INotification
    {
        public int Order => 1;

        public async Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Entries.Add("outer:before");
            await next(cancellationToken).ConfigureAwait(false);
            log.Entries.Add("outer:after");
        }
    }

    public sealed class InnerNotificationBehavior(NotificationLog log) : INotificationBehavior<AuditedNotification>
    {
        public int Order => 2;

        public async Task Handle(AuditedNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken)
        {
            log.Entries.Add("inner:before");
            await next(cancellationToken).ConfigureAwait(false);
            log.Entries.Add("inner:after");
        }
    }

    public sealed class CustomReverseNotificationPublisher(NotificationLog log) : INotificationPublisher
    {
        public async Task Publish<TNotification>(
            IEnumerable<INotificationHandler<TNotification>> handlers,
            TNotification notification,
            CancellationToken cancellationToken)
            where TNotification : INotification
        {
            log.Entries.Add("custom-publisher");
            foreach (var handler in handlers.Reverse())
            {
                await handler.Handle(notification, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public async Task Publish_invokes_notification_behaviors_and_custom_notification_publisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<NotificationLog>();
        services.AddSingleton<INotificationPublisher, CustomReverseNotificationPublisher>();
        services.AddSimpleMediator(options =>
        {
            options.RegisterAssembly(typeof(StreamTelemetryNotificationTests).Assembly,
                type => type == typeof(AuditedNotificationHandler));
            options.AddNotificationBehavior(typeof(OuterNotificationBehavior<>));
            options.AddNotificationBehavior(typeof(InnerNotificationBehavior));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

        await publisher.Publish(new AuditedNotification("hello"));

        var log = provider.GetRequiredService<NotificationLog>();
        Assert.Equal(
            [
                "outer:before",
                "inner:before",
                "custom-publisher",
                "handler:hello",
                "inner:after",
                "outer:after",
            ],
            log.Entries);
    }

    public sealed record TelemetryPing(string Text) : IRequest<string>;

    public sealed class TelemetryPingHandler : IRequestHandler<TelemetryPing, string>
    {
        public Task<string> Handle(TelemetryPing request, CancellationToken cancellationToken)
            => Task.FromResult(request.Text.ToUpperInvariant());
    }

    [Fact]
    public async Task EnableTelemetry_emits_Activity_for_Send_and_Publish()
    {
        var startedActivities = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SimpleMediatorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (startedActivities)
                {
                    startedActivities.Add(activity.OperationName);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddSingleton<NotificationLog>();
        services.AddSimpleMediator(options =>
        {
            options.EnableTelemetry = true;
            options.RegisterAssembly(typeof(StreamTelemetryNotificationTests).Assembly,
                type => type == typeof(TelemetryPingHandler) || type == typeof(AuditedNotificationHandler));
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new TelemetryPing("abc"));
        await mediator.Publish(new AuditedNotification("evt"));

        Assert.Equal("ABC", result);
        lock (startedActivities)
        {
            Assert.Contains(nameof(TelemetryPing), startedActivities);
            Assert.Contains(nameof(AuditedNotification), startedActivities);
        }
    }
}
