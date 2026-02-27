#nullable enable
using System;

namespace Orleans.Reminders.Cron.Internal
{
    internal static class DateTimeHelper
    {
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        public static DateTime FloorToSeconds(DateTime dateTime)
        {
            return dateTime.AddTicks(-GetExtraTicks(dateTime.Ticks));
        }

        public static bool IsRound(DateTime dateTime)
        {
            return GetExtraTicks(dateTime.Ticks) == 0;
        }

        private static long GetExtraTicks(long ticks)
        {
            return ticks % OneSecond.Ticks;
        }
    }
}
