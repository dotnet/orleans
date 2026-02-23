#nullable enable
using System;

namespace Orleans.Reminders.Cron.Internal
{
    internal static class CalendarHelper
    {
        private const int DaysPerWeekCount = 7;

        public static bool IsGreaterThan(int year1, int month1, int day1, int year2, int month2, int day2)
        {
            if (year1 != year2) return year1 > year2;
            if (month1 != month2) return month1 > month2;
            return day1 > day2;
        }

        public static long DateTimeToTicks(int year, int month, int day, int hour, int minute, int second)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks;
        }

        public static void FillDateTimeParts(
            long ticks,
            out int second,
            out int minute,
            out int hour,
            out int day,
            out int month,
            out int year)
        {
            var value = new DateTime(ticks, DateTimeKind.Utc);

            second = value.Second;
            if (ticks % TimeSpan.TicksPerSecond != 0)
            {
                // Preserve scheduler semantics: non-round timestamps move to the next second.
                second++;
            }

            minute = value.Minute;
            hour = value.Hour;
            (year, month, day) = value;
        }

        public static DayOfWeek GetDayOfWeek(int year, int month, int day)
        {
            return new DateTime(year, month, day).DayOfWeek;
        }

        public static int GetDaysInMonth(int year, int month)
        {
            return DateTime.DaysInMonth(year, month);
        }

        public static int MoveToNearestWeekDay(int year, int month, int day)
        {
            var dayOfWeek = GetDayOfWeek(year, month, day);
            if (dayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                return day;
            }

            if (dayOfWeek == DayOfWeek.Sunday)
            {
                return day == GetDaysInMonth(year, month) ? day - 2 : day + 1;
            }

            return day == CronField.DaysOfMonth.First ? day + 2 : day - 1;
        }

        public static bool IsNthDayOfWeek(int day, int n)
        {
            return day - DaysPerWeekCount * n < CronField.DaysOfMonth.First
                   && day - DaysPerWeekCount * (n - 1) >= CronField.DaysOfMonth.First;
        }

        public static bool IsLastDayOfWeek(int year, int month, int day)
        {
            return day + DaysPerWeekCount > GetDaysInMonth(year, month);
        }
    }
}
