using System;

namespace Orleans.Messaging;

internal static class MessagingTime
{
    public static bool IsExpired(DateTimeOffset now, DateTimeOffset timestamp, TimeSpan retention)
    {
        var nowTicks = now.UtcTicks;
        var timestampTicks = timestamp.UtcTicks;
        return nowTicks >= timestampTicks
            && nowTicks - timestampTicks >= retention.Ticks;
    }

    /// <summary>Returns the delay before retry <paramref name="attempt"/>, doubling from the initial interval to a cap.</summary>
    /// <param name="initialInterval">The delay before the first retry.</param>
    /// <param name="attempt">The one-based number of the failed attempt.</param>
    public static TimeSpan GetBackoff(TimeSpan initialInterval, int attempt) =>
        TimeSpan.FromTicks(initialInterval.Ticks
            * (1L << Math.Clamp(attempt - 1, 0, Configuration.DurableMessagingOptions.MaximumBackoffExponent)));

    public static DateTimeOffset AddClamped(DateTimeOffset timestamp, TimeSpan duration)
    {
        var utcTicks = timestamp.UtcDateTime.Ticks;
        var remainingTicks = DateTimeOffset.MaxValue.Ticks - utcTicks;
        return new DateTimeOffset(utcTicks + Math.Min(duration.Ticks, remainingTicks), TimeSpan.Zero);
    }
}
