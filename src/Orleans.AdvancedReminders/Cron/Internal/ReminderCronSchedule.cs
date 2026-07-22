#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.AdvancedReminders.Cron.Internal;

internal sealed class ReminderCronSchedule
{
    private static readonly ConcurrentDictionary<CacheKey, ReminderCronSchedule> Cache = new();
    private static readonly ConcurrentQueue<CacheKey> CacheInsertionOrder = new();
    internal const int MaxCacheEntries = 1_024;
    internal static int CacheCount => Cache.Count;

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

        var key = new CacheKey(expressionText.Trim(), NormalizeInputTimeZoneId(timeZoneId));
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var zone = ResolveTimeZoneOrDefault(key.TimeZoneId);
        var created = new ReminderCronSchedule(
            ReminderCronExpression.Parse(key.ExpressionText),
            zone,
            NormalizeTimeZoneIdForStorage(zone));
        var result = Cache.GetOrAdd(key, created);
        if (ReferenceEquals(result, created))
        {
            CacheInsertionOrder.Enqueue(key);
            TrimCache();
        }

        return result;
    }

    public static ReminderCronSchedule Parse(ReminderCronExpression expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var zone = timeZone ?? TimeZoneInfo.Utc;
        return new ReminderCronSchedule(expression, zone, NormalizeTimeZoneIdForStorage(zone));
    }

    public static string? NormalizeTimeZoneIdForStorage(TimeZoneInfo? timeZone)
    {
        if (timeZone is null)
        {
            return null;
        }

        TimeZoneInfo systemTimeZone;
        try
        {
            systemTimeZone = IsUtc(timeZone) ? TimeZoneInfo.Utc : ResolveTimeZone(timeZone.Id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                $"Time zone '{timeZone.Id}' is not available from the system time-zone database and cannot be stored in a durable reminder.",
                nameof(timeZone),
                exception);
        }

        if (!timeZone.HasSameRules(systemTimeZone))
        {
            throw new ArgumentException(
                $"Time zone '{timeZone.Id}' uses custom adjustment rules which cannot be stored in a durable reminder.",
                nameof(timeZone));
        }

        if (IsUtc(timeZone))
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

    private static string? NormalizeInputTimeZoneId(string? timeZoneId)
        => string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim();

    private static void TrimCache()
    {
        while (Cache.Count > MaxCacheEntries && CacheInsertionOrder.TryDequeue(out var oldest))
        {
            Cache.TryRemove(oldest, out _);
        }
    }

    private readonly record struct CacheKey(string ExpressionText, string? TimeZoneId);
}
