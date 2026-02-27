#nullable enable
using System;

namespace Orleans.Reminders.Cron.Internal
{
    internal sealed class CronField
    {
        private static readonly string[] MonthNames =
        {
            String.Empty, "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"
        };

        private static readonly string[] DayOfWeekNames =
        {
            "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"
        };

        private static readonly int[] MonthNamesArray = Array.ConvertAll(MonthNames, static name =>
            name == String.Empty ? 0 : name[0] | (name[1] << 8) | (name[2] << 16));
        private static readonly int[] DayOfWeekNamesArray = Array.ConvertAll(DayOfWeekNames, static name =>
            name[0] | (name[1] << 8) | (name[2] << 16));

        // 0 and 7 are both Sunday, for compatibility reasons.
        public static readonly CronField DaysOfWeek = new CronField("Days of week", 0, 7, DayOfWeekNamesArray, false);

        public static readonly CronField Months = new CronField("Months", 1, 12, MonthNamesArray, false);
        public static readonly CronField DaysOfMonth = new CronField("Days of month", 1, 31, null, false);
        public static readonly CronField Hours = new CronField("Hours", 0, 23, null, true);
        public static readonly CronField Minutes = new CronField("Minutes", 0, 59, null, true);
        public static readonly CronField Seconds = new CronField("Seconds", 0, 59, null, true);

        public readonly string Name;
        public readonly int First;
        public readonly int Last;
        public readonly int[]? Names;
        public readonly bool CanDefineInterval;
        public readonly ulong AllBits;

        private CronField(string name, int first, int last, int[]? names, bool canDefineInterval)
        {
            Name = name;
            First = first;
            Last = last;
            Names = names;
            CanDefineInterval = canDefineInterval;
            for (int i = First; i <= Last; i++)
            {
                AllBits = AllBits | (1UL << i);
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
