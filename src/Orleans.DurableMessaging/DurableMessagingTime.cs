namespace Orleans.DurableMessaging;

internal static class DurableMessagingTime
{
    public static bool IsExpired(DateTimeOffset now, DateTimeOffset timestamp, TimeSpan retention) =>
        now - timestamp >= retention;

    public static DateTimeOffset AddClamped(DateTimeOffset timestamp, TimeSpan duration)
    {
        var utcTicks = timestamp.UtcDateTime.Ticks;
        var remainingTicks = DateTimeOffset.MaxValue.Ticks - utcTicks;
        return new DateTimeOffset(utcTicks + Math.Min(duration.Ticks, remainingTicks), TimeSpan.Zero);
    }
}
