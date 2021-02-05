using System;


namespace Orleans.Internal
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    public static class StandardExtensions
    {
        public static TimeSpan Multiply(this TimeSpan timeSpan, double value)
        {
#if NETCOREAPP
            return timeSpan * value;
#else
            double ticksD = checked((double)timeSpan.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
#endif
        }

        public static TimeSpan Divide(this TimeSpan timeSpan, double value)
        {
#if NETCOREAPP
            return timeSpan / value;
#else
            double ticksD = checked((double)timeSpan.Ticks / value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
#endif
        }

        public static double Divide(this TimeSpan first, TimeSpan second)
        {
#if NETCOREAPP
            return first / second;
#else
            double ticks1 = (double)first.Ticks;
            double ticks2 = (double)second.Ticks;
            return ticks1 / ticks2;
#endif
        }

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
