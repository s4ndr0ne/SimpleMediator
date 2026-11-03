#if !NETSTANDARD2_0
using System.Diagnostics;
using System.Diagnostics.Metrics;
#endif

namespace SimpleMediator;

/// <summary>
/// Built-in observability names and helpers for OpenTelemetry tracing and metrics.
/// </summary>
public static class SimpleMediatorDiagnostics
{
    /// <summary>
    /// The name of the <c>ActivitySource</c> emitted by SimpleMediator when
    /// <see cref="SimpleMediatorOptions.EnableTelemetry"/> is enabled (<c>"SimpleMediator"</c>).
    /// </summary>
    public const string ActivitySourceName = "SimpleMediator";

    /// <summary>
    /// The name of the <c>Meter</c> emitted by SimpleMediator when
    /// <see cref="SimpleMediatorOptions.EnableTelemetry"/> is enabled (<c>"SimpleMediator"</c>).
    /// </summary>
    public const string MeterName = "SimpleMediator";

#if !NETSTANDARD2_0
    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "mediator.request.duration",
            unit: "ms",
            description: "Duration of mediator request handling in milliseconds.");

    internal static readonly Histogram<double> NotificationDuration =
        Meter.CreateHistogram<double>(
            "mediator.notification.duration",
            unit: "ms",
            description: "Duration of mediator notification publishing in milliseconds.");

    internal static bool HasTelemetryListeners()
        => ActivitySource.HasListeners() || RequestDuration.Enabled || NotificationDuration.Enabled;

    private static readonly KeyValuePair<string, object?> StatusOkTag = new("mediator.status", "ok");
    private static readonly KeyValuePair<string, object?> StatusCanceledTag = new("mediator.status", "canceled");
    private static readonly KeyValuePair<string, object?> StatusErrorTag = new("mediator.status", "error");

    private static class TelemetryMetadata<T>
    {
        public static readonly string Name = typeof(T).Name;
        public static readonly string? FullName = typeof(T).FullName;
        public static readonly KeyValuePair<string, object?> RequestTypeTag = new("mediator.request.type", FullName);
        public static readonly KeyValuePair<string, object?> NotificationTypeTag = new("mediator.notification.type", FullName);
    }

    internal static async Task<TResponse> TraceRequestAsync<TRequest, TResponse>(Task<TResponse> task)
    {
        using var activity = ActivitySource.StartActivity(TelemetryMetadata<TRequest>.Name, ActivityKind.Internal);
        activity?.SetTag("mediator.request.type", TelemetryMetadata<TRequest>.FullName);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await task.ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            if (RequestDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                RequestDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TRequest>.RequestTypeTag,
                    StatusOkTag);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Canceled");
            if (RequestDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                RequestDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TRequest>.RequestTypeTag,
                    StatusCanceledTag);
            }

            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (RequestDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                RequestDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TRequest>.RequestTypeTag,
                    StatusErrorTag,
                    new KeyValuePair<string, object?>("error.type", ex.GetType().FullName));
            }

            throw;
        }
    }

    internal static async Task TraceNotificationAsync<TNotification>(Task task)
    {
        using var activity = ActivitySource.StartActivity(TelemetryMetadata<TNotification>.Name, ActivityKind.Internal);
        activity?.SetTag("mediator.notification.type", TelemetryMetadata<TNotification>.FullName);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await task.ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            if (NotificationDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                NotificationDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TNotification>.NotificationTypeTag,
                    StatusOkTag);
            }
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Canceled");
            if (NotificationDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                NotificationDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TNotification>.NotificationTypeTag,
                    StatusCanceledTag);
            }

            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (NotificationDuration.Enabled)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                NotificationDuration.Record(
                    elapsedMs,
                    TelemetryMetadata<TNotification>.NotificationTypeTag,
                    StatusErrorTag,
                    new KeyValuePair<string, object?>("error.type", ex.GetType().FullName));
            }

            throw;
        }
    }
#endif
}
