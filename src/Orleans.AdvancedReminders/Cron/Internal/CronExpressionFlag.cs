#nullable enable
using System;

namespace Orleans.AdvancedReminders.Cron.Internal
{
    [Flags]
    internal enum CronExpressionFlag : byte
    {
        DayOfMonthLast = 0b00001,
        DayOfWeekLast  = 0b00010,
        Interval       = 0b00100,
        NearestWeekday = 0b01000,
        NthDayOfWeek   = 0b10000
    }
}
