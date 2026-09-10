using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Orleans.AdvancedReminders.Cron.Internal;

/// <summary>Maps a local date to UTC windows with a constant offset, including clock gaps.</summary>
internal sealed class CronTimeZoneCalendar(TimeZoneInfo zone)
{
    private static readonly ConditionalWeakTable<TimeZoneInfo, CronTimeZoneCalendar> Calendars = new();
    private readonly TimeZoneInfo.AdjustmentRule[] _rules = zone.GetAdjustmentRules();
    private readonly ConcurrentDictionary<DateTime, Window[]> _dates = new();
    private readonly ConcurrentQueue<DateTime> _insertionOrder = new();
    private static readonly long EndOfTime = DateTime.MaxValue.Ticks + 1;
    private const long MaximumOffset = 14 * TimeSpan.TicksPerHour;

    public static CronTimeZoneCalendar For(TimeZoneInfo zone) => Calendars.GetValue(zone, static value => new(value));

    public Window[] GetWindows(DateTime date)
    {
        if (_dates.TryGetValue(date, out var cached))
        {
            return cached;
        }

        var windows = BuildWindows(date);
        if (_dates.TryAdd(date, windows))
        {
            _insertionOrder.Enqueue(date);
            while (_dates.Count > 64 && _insertionOrder.TryDequeue(out var oldest))
            {
                _dates.TryRemove(oldest, out _);
            }
        }

        return windows;
    }

    private Window[] BuildWindows(DateTime date)
    {
        var dayStart = date.Ticks;
        var dayEnd = dayStart + TimeSpan.TicksPerDay;
        var utcStart = Math.Max(0, dayStart - MaximumOffset);
        var utcEnd = Math.Min(EndOfTime, dayEnd + MaximumOffset);
        var boundaries = new SortedSet<long> { utcStart, utcEnd };
        var localBoundaries = new HashSet<long>();
        var offsets = new HashSet<long> { zone.BaseUtcOffset.Ticks };
        foreach (var rule in _rules)
        {
            if (rule.DateStart.Ticks > dayEnd + 2 * TimeSpan.TicksPerDay
                || rule.DateEnd.Ticks < dayStart - 2 * TimeSpan.TicksPerDay)
            {
                continue;
            }

            var standard = zone.BaseUtcOffset + rule.BaseUtcOffsetDelta;
            offsets.Add(standard.Ticks);
            offsets.Add((standard + rule.DaylightDelta).Ticks);
            localBoundaries.Add(rule.DateStart.Date.Ticks);
            localBoundaries.Add(rule.DateEnd.Date.Ticks + TimeSpan.TicksPerDay);
            for (var year = Math.Max(1, date.Year - 1); year <= Math.Min(9999, date.Year + 1); year++)
            {
                localBoundaries.Add(TransitionTicks(year, rule.DaylightTransitionStart));
                var end = TransitionTicks(year, rule.DaylightTransitionEnd);
                localBoundaries.Add(end);
                // TZif rules can describe an inclusive millisecond, whereas Windows rules
                // describe the first instant of the new offset. Include both boundaries.
                localBoundaries.Add(end + TimeSpan.TicksPerMillisecond);
            }
        }

        foreach (var local in localBoundaries)
        {
            foreach (var offset in offsets)
            {
                var utc = local - offset;
                if (utc > utcStart && utc < utcEnd)
                {
                    boundaries.Add(utc);
                }
            }
        }

        var result = new List<Window>();
        var points = boundaries.ToArray();
        long? previousOffset = null;
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var offset = zone.GetUtcOffset(new DateTime(start, DateTimeKind.Utc)).Ticks;
            if (previousOffset is { } before && offset > before)
            {
                AddWindow(start + before, start + offset, start, isGap: true);
            }

            AddWindow(start + offset, end + offset, offset, isGap: false);
            previousOffset = offset;
        }

        return [.. result];

        void AddWindow(long startLocal, long endLocal, long mapping, bool isGap)
        {
            var start = Math.Max(dayStart, startLocal);
            var end = Math.Min(dayEnd, endLocal);
            if (start < end)
            {
                var wallTime = new DateTime(start, DateTimeKind.Unspecified);
                var repeated = !isGap && zone.IsAmbiguousTime(wallTime)
                    && mapping != zone.GetAmbiguousTimeOffsets(wallTime).Max().Ticks;
                result.Add(new Window(start, end, mapping, isGap, repeated));
            }
        }
    }

    private static long TransitionTicks(int year, TimeZoneInfo.TransitionTime transition)
    {
        var days = DateTime.DaysInMonth(year, transition.Month);
        var day = Math.Min(transition.Day, days);
        if (!transition.IsFixedDateRule)
        {
            var first = (int)new DateTime(year, transition.Month, 1).DayOfWeek;
            day = 1 + ((int)transition.DayOfWeek - first + 7) % 7 + (transition.Week - 1) * 7;
            if (day > days)
            {
                day -= 7;
            }
        }

        return new DateTime(year, transition.Month, day).Ticks + transition.TimeOfDay.TimeOfDay.Ticks;
    }

    // Mapping is a UTC offset for ordinary windows and the catch-up UTC instant for gaps.
    internal readonly record struct Window(long StartLocal, long EndLocal, long Mapping, bool IsGap, bool IsRepeated);
}
