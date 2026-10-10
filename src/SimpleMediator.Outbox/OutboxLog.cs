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

    [LoggerMessage(4, LogLevel.Warning, "Lost the lease for outbox message {OutboxMessageId}; dispatch will stop.")]
    internal static partial void LeaseLost(ILogger logger, Guid outboxMessageId);

    [LoggerMessage(5, LogLevel.Error, "Failed to renew the lease for outbox message {OutboxMessageId}.")]
    internal static partial void LeaseRenewalFailed(ILogger logger, Exception exception, Guid outboxMessageId);

    [LoggerMessage(6, LogLevel.Error, "Failed to mark outbox message {OutboxMessageId} as processed after dispatch.")]
    internal static partial void MarkProcessedFailed(ILogger logger, Exception exception, Guid outboxMessageId);

    [LoggerMessage(7, LogLevel.Error, "Failed to prepare outbox message {OutboxMessageId} for dispatch.")]
    internal static partial void DispatchSetupFailed(ILogger logger, Exception exception, Guid outboxMessageId);
}
