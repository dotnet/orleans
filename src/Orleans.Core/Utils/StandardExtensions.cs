using System;


namespace Orleans.Internal
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    internal static class StandardExtensions
    {
        public static TimeSpan Max(TimeSpan first, TimeSpan second)
        {
            return first >= second ? first : second;
        }

        public static TimeSpan Min(TimeSpan first, TimeSpan second)
        {
            return first < second ? first : second;
        }
    }
}
