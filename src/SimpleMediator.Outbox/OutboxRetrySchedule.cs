using System.Buffers.Binary;

namespace SimpleMediator.Outbox;

/// <summary>Computes the exponential backoff delay applied before an outbox message is retried.</summary>
internal static class OutboxRetrySchedule
{
    /// <summary>Returns the delay before the next delivery attempt of the supplied lease.</summary>
    internal static TimeSpan GetDelay(OutboxOptions options, OutboxLease lease)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lease);

        // OutboxLease.Attempts counts failures already recorded by the store, so the first retry is
        // attempt number 1 and uses the base delay unchanged.
        var attemptNumber = Math.Max(0, lease.Attempts) + 1;
        return GetDelay(options, attemptNumber, lease.LeaseToken);
    }

    /// <summary>Returns the delay before <paramref name="attemptNumber"/> (1-based).</summary>
    internal static TimeSpan GetDelay(OutboxOptions options, int attemptNumber, Guid salt)
    {
        ArgumentNullException.ThrowIfNull(options);

        var exponent = Math.Max(0, attemptNumber - 1);
        var multiplier = Math.Pow(options.RetryBackoffMultiplier, exponent);

        // The cap never drops below the base delay: a RetryDelay larger than MaxRetryDelay simply means
        // the delay stays at the base value instead of failing validation.
        var maxTicks = Math.Max(options.MaxRetryDelay.Ticks, options.RetryDelay.Ticks);
        var ticks = options.RetryDelay.Ticks * multiplier;

        if (!double.IsFinite(ticks) || ticks > maxTicks)
        {
            ticks = maxTicks;
        }

        var jitterFactor = options.RetryJitterFactor;
        if (jitterFactor > 0 && ticks > 0)
        {
            // Deterministic symmetric jitter in [-1, 1] keeps retry storms apart while remaining
            // reproducible for a given lease token.
            var jitter = GetUnitInterval(salt) * 2d - 1d;
            ticks = Math.Clamp(ticks + (ticks * jitterFactor * jitter), 0d, maxTicks);
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    private static double GetUnitInterval(Guid salt)
    {
        Span<byte> bytes = stackalloc byte[16];
        salt.TryWriteBytes(bytes);

        // Take the high 53 bits so every distinct value maps to a distinct double in [0, 1).
        var sample = BinaryPrimitives.ReadUInt64LittleEndian(bytes) >> 11;
        return sample / (double)(1UL << 53);
    }
}
