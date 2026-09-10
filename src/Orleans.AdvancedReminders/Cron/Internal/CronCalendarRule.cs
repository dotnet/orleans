namespace Orleans.AdvancedReminders.Cron.Internal;

/// <summary>Calendar predicates for month days and weekdays, combined using AND.</summary>
internal sealed class CronCalendarRule
{
    internal static readonly string[] Weekdays = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private readonly CronValueSet _monthDays;
    private readonly CronValueSet _weekDays;
    private readonly int? _lastDayOffset;
    private readonly int? _nearestWeekday;
    private readonly bool _lastWeekday;
    private readonly int? _ordinalWeekday;
    private readonly int _weekday;

    public CronCalendarRule(string monthDay, string weekDay)
    {
        monthDay = monthDay.ToUpperInvariant();
        weekDay = weekDay.ToUpperInvariant();
        var nearest = monthDay.EndsWith('W');
        var day = nearest ? monthDay[..^1] : monthDay;
        if (day == "L" || day.StartsWith("L-", StringComparison.Ordinal))
        {
            _lastDayOffset = day == "L" ? 0 : CronValueSet.ReadNumber(day[2..], 0, 30);
            _lastWeekday = nearest;
        }
        else if (nearest)
        {
            _nearestWeekday = CronValueSet.ReadNumber(day, 1, 31);
        }
        else
        {
            _monthDays = CronValueSet.Parse(day, 1, 31);
        }

        if (weekDay.EndsWith('L'))
        {
            _weekday = CronValueSet.ReadValue(weekDay[..^1], 0, 7, Weekdays) % 7;
            _ordinalWeekday = -1;
        }
        else if (weekDay.Contains('#', StringComparison.Ordinal))
        {
            var parts = weekDay.Split('#');
            if (parts.Length != 2)
            {
                throw CronValueSet.Invalid(weekDay);
            }

            _weekday = CronValueSet.ReadValue(parts[0], 0, 7, Weekdays) % 7;
            _ordinalWeekday = CronValueSet.ReadNumber(parts[1], 1, 5);
        }
        else
        {
            _weekDays = CronValueSet.Parse(weekDay, 0, 7, Weekdays, sundayAlias: true);
        }
    }

    public CronValueSet GetDays(int year, int month)
    {
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var target = _lastDayOffset is { } offset ? daysInMonth - offset : _nearestWeekday;
        ulong days;
        if (target is { } day)
        {
            if (day < 1 || day > daysInMonth)
            {
                return default;
            }

            if (_lastWeekday || _nearestWeekday.HasValue)
            {
                var weekday = new DateTime(year, month, day).DayOfWeek;
                day += weekday switch
                {
                    DayOfWeek.Saturday => day == 1 ? 2 : -1,
                    DayOfWeek.Sunday => day == daysInMonth ? -2 : 1,
                    _ => 0,
                };
            }

            days = 1UL << day;
        }
        else
        {
            days = _monthDays.Bits & ((1UL << (daysInMonth + 1)) - 2);
        }

        if (days == 0 || _ordinalWeekday is null && _weekDays.Bits == 0b1111111)
        {
            return new CronValueSet(days);
        }

        var firstWeekday = (int)new DateTime(year, month, 1).DayOfWeek;
        if (_ordinalWeekday is { } ordinal)
        {
            var first = 1 + (_weekday - firstWeekday + 7) % 7;
            var selected = ordinal == -1 ? first + (daysInMonth - first) / 7 * 7 : first + (ordinal - 1) * 7;
            return new CronValueSet(selected <= daysInMonth ? days & (1UL << selected) : 0);
        }

        ulong weekdays = 0;
        for (var weekday = _weekDays.Next(0); weekday >= 0; weekday = _weekDays.Next(weekday + 1))
        {
            for (var selectedDay = 1 + (weekday - firstWeekday + 7) % 7; selectedDay <= daysInMonth; selectedDay += 7)
            {
                weekdays |= 1UL << selectedDay;
            }
        }

        return new CronValueSet(days & weekdays);
    }
}
