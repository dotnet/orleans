using System;


namespace Orleans.Internal
{
    /// <summary>
    /// Provides extension methods for <see cref="TimeSpan"/> values.
    /// </summary>
    internal static class TimeSpanExtensions
    {
        public static TimeSpan Max(this TimeSpan first, TimeSpan second) => first >= second ? first : second;

        public static TimeSpan Min(this TimeSpan first, TimeSpan second) => first < second ? first : second;
    }
}
