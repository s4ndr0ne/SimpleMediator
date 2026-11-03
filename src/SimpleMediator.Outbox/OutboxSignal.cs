using System.Threading.Channels;

namespace SimpleMediator.Outbox;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the package's dependency-injection registration.")]
internal sealed class OutboxSignal : IOutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    public void Pulse() => _channel.Writer.TryWrite(true);

    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (_channel.Reader.TryRead(out _))
        {
            return true;
        }

        // Wait directly on the channel's pooled ValueTask<bool> with a timeout-backed token instead
        // of converting WaitToReadAsync to a heap-allocated Task via .AsTask() + Task.Delay + Task.WhenAny.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ITimer? customTimer = null;
        if (ReferenceEquals(timeProvider, TimeProvider.System))
        {
            timeoutCts.CancelAfter(timeout);
        }
        else
        {
            customTimer = timeProvider.CreateTimer(
                static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                },
                timeoutCts,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        try
        {
            if (await _channel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                _ = _channel.Reader.TryRead(out _);
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (customTimer is not null)
            {
                await customTimer.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
