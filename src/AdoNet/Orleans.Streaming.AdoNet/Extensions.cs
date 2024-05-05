namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Internal syntax sugar.
/// </summary>
internal static class Extensions
{
    /// <inheritdoc cref="Math.Ceiling(double)"/>
    public static int Int32Ceiling(this double value) => (int)Math.Ceiling(value);

    /// <summary>
    /// Rounds up the specified time span to the nearest upper second and returns the total number of seconds as an integer.
    /// </summary>
    public static int TotalSecondsCeiling(this TimeSpan value) => value.TotalSeconds.Int32Ceiling();

    /// <summary>
    /// Rounds up the specified time span to the nearest upper second and returns the total number of seconds as an integer.
    /// </summary>
    public static TimeSpan SecondsCeiling(this TimeSpan value) => TimeSpan.FromSeconds(value.TotalSecondsCeiling());
}