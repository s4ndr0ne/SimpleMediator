using Microsoft.Extensions.Logging;

namespace SimpleMediator.Outbox.SqlServer;

internal static partial class SqlServerOutboxLog
{
    [LoggerMessage(1, LogLevel.Information, "Purged {PurgedCount} outbox messages past their retention window.")]
    internal static partial void PurgedMessages(ILogger logger, int purgedCount);

    [LoggerMessage(2, LogLevel.Error, "Failed to purge outbox messages past their retention window.")]
    internal static partial void PurgeFailed(ILogger logger, Exception exception);
}
