namespace Orleans.AdvancedReminders.Cron.Internal;

/// <summary>Finds occurrences by selecting matching months, days, and clock values.</summary>
internal sealed class CronSchedulePattern(
    CronValueSet seconds,
    CronValueSet minutes,
    CronValueSet hours,
    CronValueSet months,
    CronCalendarRule calendar,
    bool repeatsDuringClockRollback)
{
    private bool _calendarHasNoMatches;

    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
        => GetNextOccurrence(fromUtc, TimeZoneInfo.Utc, inclusive);

    public DateTime? GetNextOccurrence(DateTime fromUtc, TimeZoneInfo zone, bool inclusive = false)
    {
        if (Volatile.Read(ref _calendarHasNoMatches) || !inclusive && fromUtc.Ticks == DateTime.MaxValue.Ticks)
        {
            return null;
        }

        var minimumUtc = fromUtc.Ticks + (inclusive ? 0 : 1);
        var isUtc = ReminderCronTimeZoneResolver.IsUtc(zone);
        var firstDate = isUtc ? fromUtc.Date : TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone).Date;
        // Include preceding dates for catch-up after a skipped date. Compare results
        // in UTC so a rollback across midnight cannot reorder occurrences.
        if (!isUtc)
        {
            firstDate = firstDate.AddDays(-Math.Min(2, (firstDate - DateTime.MinValue).Days));
        }

        var fullCycle = firstDate.Year < 9600;
        var lastDate = fullCycle ? firstDate.AddYears(400) : DateTime.MaxValue.Date;
        var date = NextDate(firstDate, lastDate);
        if (date is null && fullCycle)
        {
            // Date and weekday combinations repeat every 400 Gregorian years.
            // A query near DateTime.MaxValue cannot prove global impossibility.
            Volatile.Write(ref _calendarHasNoMatches, true);
        }

        long? best = null;
        while (date is { } day)
        {
            if (isUtc)
            {
                var next = NextTime(day, Math.Max(day.Ticks, minimumUtc), day.Ticks + TimeSpan.TicksPerDay);
                if (next is { } ticks)
                {
                    return new DateTime(ticks, DateTimeKind.Utc);
                }
            }
            else
            {
                foreach (var window in CronTimeZoneCalendar.For(zone).GetWindows(day))
                {
                    if (!repeatsDuringClockRollback && window.IsRepeated)
                    {
                        continue;
                    }

                    var lower = window.IsGap ? window.StartLocal : Math.Max(window.StartLocal, minimumUtc + window.Mapping);
                    var next = NextTime(day, lower, window.EndLocal);
                    if (next is not { } wallTicks)
                    {
                        continue;
                    }

                    // Cron has whole-second resolution even when an OS rule ends at .999 milliseconds.
                    var utcTicks = window.IsGap
                        ? (window.Mapping + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond
                        : wallTicks - window.Mapping;
                    if (utcTicks < minimumUtc || utcTicks > DateTime.MaxValue.Ticks)
                    {
                        continue;
                    }

                    best = best is null ? utcTicks : Math.Min(best.Value, utcTicks);
                }
            }

            if (day == DateTime.MaxValue.Date)
            {
                break;
            }

            if (best is { } found)
            {
                lastDate = new DateTime(Math.Min(DateTime.MaxValue.Ticks, found + 14 * TimeSpan.TicksPerHour)).Date;
            }

            date = NextDate(day.AddDays(1), lastDate);
        }

        return best is { } result ? new DateTime(result, DateTimeKind.Utc) : null;
    }

    private DateTime? NextDate(DateTime from, DateTime limit)
    {
        var year = from.Year;
        var month = from.Month;
        var firstDay = from.Day;
        while (year <= limit.Year)
        {
            var selectedMonth = months.Next(month);
            if (selectedMonth < 0)
            {
                year++;
                month = 1;
                firstDay = 1;
                continue;
            }

            if (year == limit.Year && selectedMonth > limit.Month)
            {
                break;
            }

            var day = calendar.GetDays(year, selectedMonth).Next(selectedMonth == month ? firstDay : 1);
            if (day >= 0)
            {
                var candidate = new DateTime(year, selectedMonth, day);
                return candidate <= limit ? candidate : null;
            }

            month = selectedMonth + 1;
            firstDay = 1;
        }

        return null;
    }

    private long? NextTime(DateTime date, long lower, long upper)
    {
        if (lower >= upper)
        {
            return null;
        }

        var secondOfDay = (int)((lower - date.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
        for (var hour = hours.Next(secondOfDay / 3600); hour >= 0; hour = hours.Next(hour + 1))
        {
            var firstMinute = Math.Max(0, secondOfDay - hour * 3600) / 60;
            for (var minute = minutes.Next(firstMinute); minute >= 0; minute = minutes.Next(minute + 1))
            {
                var start = hour * 3600 + minute * 60;
                var second = seconds.Next(Math.Max(0, secondOfDay - start));
                if (second >= 0)
                {
                    var ticks = date.Ticks + (start + second) * TimeSpan.TicksPerSecond;
                    return ticks < upper ? ticks : null;
                }
            }
        }

        return null;
    }

    public IEnumerable<DateTime> GetOccurrences(DateTime fromUtc, DateTime toUtc, bool fromInclusive = true, bool toInclusive = false)
        => GetOccurrences(fromUtc, toUtc, TimeZoneInfo.Utc, fromInclusive, toInclusive);

    public IEnumerable<DateTime> GetOccurrences(DateTime fromUtc, DateTime toUtc, TimeZoneInfo zone, bool fromInclusive = true, bool toInclusive = false)
    {
        if (fromUtc > toUtc)
        {
            throw new ArgumentException("The start of the occurrence range must not exceed its end.", nameof(fromUtc));
        }

        var next = GetNextOccurrence(fromUtc, zone, fromInclusive);
        while (next is { } occurrence && (occurrence < toUtc || toInclusive && occurrence == toUtc))
        {
            yield return occurrence;
            next = GetNextOccurrence(occurrence, zone);
        }
    }
}
