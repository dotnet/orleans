namespace Orleans.Runtime.Dissemination;

internal static class DisseminationExpiration
{
    public static DateTimeOffset Get(TimeProvider timeProvider, TimeSpan timeToLive)
    {
        var now = timeProvider.GetUtcNow();
        return timeToLive >= DateTimeOffset.MaxValue - now
            ? DateTimeOffset.MaxValue
            : now + timeToLive;
    }
}
