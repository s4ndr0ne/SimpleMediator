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

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, timeProvider, delayCts.Token);
        var readTask = _channel.Reader.WaitToReadAsync(cancellationToken).AsTask();

        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (completed == readTask)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            if (await readTask.ConfigureAwait(false))
            {
                _ = _channel.Reader.TryRead(out _);
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
