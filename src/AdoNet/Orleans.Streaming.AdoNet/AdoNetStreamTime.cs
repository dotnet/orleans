namespace Orleans.Streaming.AdoNet;

internal static class AdoNetStreamTime
{
    private static readonly TimeSpan MaxSqlInterval = TimeSpan.FromSeconds(int.MaxValue);

    internal static bool IsValidSqlInterval(TimeSpan value)
        => value >= TimeSpan.FromSeconds(1) && value <= MaxSqlInterval;

    internal static int ToSqlSeconds(TimeSpan value)
    {
        if (value < TimeSpan.Zero || value > MaxSqlInterval)
        {
            throw new OverflowException("The interval does not fit in SQL integer seconds.");
        }

        var seconds = value.Ticks / TimeSpan.TicksPerSecond;
        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            seconds++;
        }

        return checked((int)seconds);
    }
}
