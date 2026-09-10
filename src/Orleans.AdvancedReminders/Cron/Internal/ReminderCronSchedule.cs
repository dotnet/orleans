#nullable enable
using System.Collections.Concurrent;

namespace Orleans.AdvancedReminders.Cron.Internal;

internal sealed class ReminderCronSchedule
{
    private static readonly ConcurrentDictionary<CacheKey, Lazy<ReminderCronSchedule>> Cache = new();
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
            return cached.Value;
        }

        var created = new Lazy<ReminderCronSchedule>(
            () =>
            {
                var zone = ResolveTimeZoneOrDefault(key.TimeZoneId);
                return new ReminderCronSchedule(
                    ReminderCronExpression.Parse(key.ExpressionText),
                    zone,
                    NormalizeTimeZoneIdForStorage(zone));
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        var result = Cache.GetOrAdd(key, created);
        ReminderCronSchedule schedule;
        try
        {
            schedule = result.Value;
        }
        catch
        {
            ((ICollection<KeyValuePair<CacheKey, Lazy<ReminderCronSchedule>>>)Cache)
                .Remove(new(key, result));
            throw;
        }

        if (ReferenceEquals(result, created))
        {
            CacheInsertionOrder.Enqueue(key);
            TrimCache();
        }

        return schedule;
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
            systemTimeZone = ReminderCronTimeZoneResolver.IsUtc(timeZone) ? TimeZoneInfo.Utc : ReminderCronTimeZoneResolver.Resolve(timeZone.Id);
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

        if (ReminderCronTimeZoneResolver.IsUtc(timeZone))
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
            return ReminderCronTimeZoneResolver.Resolve(timeZoneId.Trim());
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ReminderCronParseException($"Unknown time zone id '{timeZoneId}'.", exception);
        }
    }

    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
    {
        return ReminderCronTimeZoneResolver.IsUtc(TimeZone)
            ? Expression.GetNextOccurrence(fromUtc, inclusive)
            : Expression.GetNextOccurrence(fromUtc, TimeZone, inclusive);
    }

    public IEnumerable<DateTime> GetOccurrences(
        DateTime fromUtc,
        DateTime toUtc,
        bool fromInclusive = true,
        bool toInclusive = false)
    {
        return ReminderCronTimeZoneResolver.IsUtc(TimeZone)
            ? Expression.GetOccurrences(fromUtc, toUtc, fromInclusive, toInclusive)
            : Expression.GetOccurrences(fromUtc, toUtc, TimeZone, fromInclusive, toInclusive);
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
