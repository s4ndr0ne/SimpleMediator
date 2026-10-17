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

    internal static async Task<TResponse> TraceRequestAsync<TRequest, TResponse>(Task<TResponse> task)
    {
        using var activity = ActivitySource.StartActivity(typeof(TRequest).Name, ActivityKind.Internal);
        activity?.SetTag("mediator.request.type", typeof(TRequest).FullName);
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
                    new KeyValuePair<string, object?>("mediator.request.type", typeof(TRequest).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "ok"));
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
                    new KeyValuePair<string, object?>("mediator.request.type", typeof(TRequest).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "canceled"));
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
                    new KeyValuePair<string, object?>("mediator.request.type", typeof(TRequest).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "error"),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().FullName));
            }

            throw;
        }
    }

    internal static async Task TraceNotificationAsync<TNotification>(Task task)
    {
        using var activity = ActivitySource.StartActivity(typeof(TNotification).Name, ActivityKind.Internal);
        activity?.SetTag("mediator.notification.type", typeof(TNotification).FullName);
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
                    new KeyValuePair<string, object?>("mediator.notification.type", typeof(TNotification).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "ok"));
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
                    new KeyValuePair<string, object?>("mediator.notification.type", typeof(TNotification).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "canceled"));
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
                    new KeyValuePair<string, object?>("mediator.notification.type", typeof(TNotification).FullName),
                    new KeyValuePair<string, object?>("mediator.status", "error"),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().FullName));
            }

            throw;
        }
    }
#endif
}
