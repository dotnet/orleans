namespace Orleans.Internal;

internal static class DateTimeExtensions
{
    public static DateTime AddClamped(this DateTime value, TimeSpan amount)
    {
        var remainingTicks = DateTime.MaxValue.Ticks - value.Ticks;
        return amount.Ticks >= remainingTicks
            ? new DateTime(DateTime.MaxValue.Ticks, value.Kind)
            : value.AddTicks(amount.Ticks);
    }
}
