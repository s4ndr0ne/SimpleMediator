namespace SimpleMediator;

/// <summary>
/// Controls how <see cref="Interfaces.IMediator.Publish{TNotification}"/> dispatches a
/// notification to its registered handlers.
/// </summary>
public enum NotificationPublishStrategy
{
    /// <summary>
    /// Handlers run one after another, awaiting each before starting the next.
    /// This is the default because it is safe to share a single DI scope (and the
    /// scoped services inside it, e.g. <c>DbContext</c>) across handlers.
    /// If a handler throws, the remaining handlers do not run.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Handlers run concurrently via <see cref="System.Threading.Tasks.Task.WhenAll(System.Threading.Tasks.Task[])"/>.
    /// Faster for independent, CPU/IO-bound handlers, but every handler shares the
    /// same <see cref="System.IServiceProvider"/>: do NOT enable this when handlers
    /// touch a shared non-thread-safe scoped service. If multiple handlers throw,
    /// an <see cref="System.AggregateException"/> with all failures is surfaced.
    /// </summary>
    Parallel = 1
}
