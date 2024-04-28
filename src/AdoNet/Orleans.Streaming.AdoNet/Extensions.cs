namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Internal syntax sugar.
/// </summary>
internal static class Extensions
{
    /// <inheritdoc cref="Math.Ceiling(double)"/>
    public static int ToInt32Ceiling(this double value) => (int)Math.Ceiling(value);

    /// <summary>
    /// Gets the smallest integer value that is equal to or greater than the total seconds of the specified time span.
    /// </summary>
    public static int ToSecondsCeiling(this TimeSpan value) => value.TotalSeconds.ToInt32Ceiling();
}