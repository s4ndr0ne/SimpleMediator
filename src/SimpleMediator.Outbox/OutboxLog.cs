using Microsoft.Extensions.Logging;

namespace SimpleMediator.Outbox;

internal static partial class OutboxLog
{
    [LoggerMessage(1, LogLevel.Error, "Failed to claim outbox messages.")]
    internal static partial void ClaimBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "Failed to dispatch outbox message {OutboxMessageId}.")]
    internal static partial void DispatchFailed(ILogger logger, Exception exception, Guid outboxMessageId);

    [LoggerMessage(3, LogLevel.Error, "Failed to release outbox message {OutboxMessageId} after a dispatch failure.")]
    internal static partial void MarkFailedFailed(ILogger logger, Exception exception, Guid outboxMessageId);
}
