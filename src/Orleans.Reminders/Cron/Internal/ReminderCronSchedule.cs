#nullable enable
using System;
using System.Collections.Generic;

namespace Orleans.Reminders.Cron.Internal;

internal sealed class ReminderCronSchedule
{
    private ReminderCronSchedule(ReminderCronExpression expression, TimeZoneInfo timeZone, string? timeZoneId)
    {
        Expression = expression;
        TimeZone = timeZone;
        TimeZoneId = timeZoneId;
    }

    public ReminderCronExpression Expression { get; }

    public TimeZoneInfo TimeZone { get; }

    public string? TimeZoneId { get; }

    public static ReminderCronSchedule Parse(string expressionText, string? timeZoneId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expressionText);

        var expression = ReminderCronExpression.Parse(expressionText);
        var zone = ResolveTimeZoneOrDefault(timeZoneId);
        return new ReminderCronSchedule(expression, zone, NormalizeTimeZoneIdForStorage(zone));
    }

    public static ReminderCronSchedule Parse(ReminderCronExpression expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var zone = timeZone ?? TimeZoneInfo.Utc;
        return new ReminderCronSchedule(expression, zone, NormalizeTimeZoneIdForStorage(zone));
    }

    public static string? NormalizeTimeZoneIdForStorage(TimeZoneInfo? timeZone)
    {
        if (timeZone is null || IsUtc(timeZone))
        {
            return null;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId))
        {
            return ianaId;
        }

        return timeZone.Id;
    }

    private static TimeZoneInfo ResolveTimeZoneOrDefault(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return ResolveTimeZone(timeZoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new CronFormatException($"Unknown time zone id '{timeZoneId}'.", exception);
        }
    }

    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
    {
        return IsUtc(TimeZone)
            ? Expression.GetNextOccurrence(fromUtc, inclusive)
            : Expression.GetNextOccurrence(fromUtc, TimeZone, inclusive);
    }

    public IEnumerable<DateTime> GetOccurrences(
        DateTime fromUtc,
        DateTime toUtc,
        bool fromInclusive = true,
        bool toInclusive = false)
    {
        return IsUtc(TimeZone)
            ? Expression.GetOccurrences(fromUtc, toUtc, fromInclusive, toInclusive)
            : Expression.GetOccurrences(fromUtc, toUtc, TimeZone, fromInclusive, toInclusive);
    }

    private static bool IsUtc(TimeZoneInfo zone)
        => string.Equals(zone.Id, TimeZoneInfo.Utc.Id, StringComparison.Ordinal);

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            }

            throw;
        }
    }
}
