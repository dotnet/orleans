#nullable enable
using System;
using System.Collections.Generic;
using Orleans.Reminders.Cron.Internal;

namespace Orleans;

/// <summary>
/// Represents a validated cron schedule for Orleans reminders.
/// </summary>
public sealed class ReminderCronExpression : IEquatable<ReminderCronExpression>
{
    private readonly CronExpression _expression;

    private ReminderCronExpression(string expressionText, CronExpression expression)
    {
        ExpressionText = expressionText;
        _expression = expression;
    }

    /// <summary>
    /// Gets the original cron expression text.
    /// </summary>
    public string ExpressionText { get; }

    /// <summary>
    /// Parses a cron expression in 5-field or 6-field (with seconds) format.
    /// </summary>
    public static ReminderCronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var format = DetectFormat(expression);
        var parsed = CronExpression.Parse(expression, format);
        return new ReminderCronExpression(expression.Trim(), parsed);
    }

    /// <summary>
    /// Attempts to parse a cron expression in 5-field or 6-field (with seconds) format.
    /// </summary>
    public static bool TryParse(string expression, out ReminderCronExpression? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            cronExpression = null;
            return false;
        }

        try
        {
            cronExpression = Parse(expression);
            return true;
        }
        catch (CronFormatException)
        {
            cronExpression = null;
            return false;
        }
        catch (ArgumentException)
        {
            cronExpression = null;
            return false;
        }
    }

    /// <summary>
    /// Gets the next occurrence in UTC.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
    {
        EnsureUtc(fromUtc, nameof(fromUtc));
        return _expression.GetNextOccurrence(fromUtc, inclusive);
    }

    /// <summary>
    /// Gets the next occurrence in UTC using the provided scheduling time zone.
    /// </summary>
    internal DateTime? GetNextOccurrence(DateTime fromUtc, TimeZoneInfo zone, bool inclusive = false)
    {
        EnsureUtc(fromUtc, nameof(fromUtc));
        ArgumentNullException.ThrowIfNull(zone);
        return _expression.GetNextOccurrence(fromUtc, zone, inclusive);
    }

    /// <summary>
    /// Gets all occurrences in the specified UTC range.
    /// </summary>
    public IEnumerable<DateTime> GetOccurrences(DateTime fromUtc, DateTime toUtc, bool fromInclusive = true, bool toInclusive = false)
    {
        EnsureUtc(fromUtc, nameof(fromUtc));
        EnsureUtc(toUtc, nameof(toUtc));
        return _expression.GetOccurrences(fromUtc, toUtc, fromInclusive, toInclusive);
    }

    /// <summary>
    /// Gets all occurrences in the specified UTC range using the provided scheduling time zone.
    /// </summary>
    internal IEnumerable<DateTime> GetOccurrences(
        DateTime fromUtc,
        DateTime toUtc,
        TimeZoneInfo zone,
        bool fromInclusive = true,
        bool toInclusive = false)
    {
        EnsureUtc(fromUtc, nameof(fromUtc));
        EnsureUtc(toUtc, nameof(toUtc));
        ArgumentNullException.ThrowIfNull(zone);
        return _expression.GetOccurrences(fromUtc, toUtc, zone, fromInclusive, toInclusive);
    }

    internal static ReminderCronExpression FromValidatedString(string expression)
    {
        var format = DetectFormat(expression);
        var parsed = CronExpression.Parse(expression, format);
        return new ReminderCronExpression(expression, parsed);
    }

    public string ToExpressionString() => ExpressionText;

    public override string ToString() => ExpressionText;

    public bool Equals(ReminderCronExpression? other)
        => other is not null && string.Equals(ExpressionText, other.ExpressionText, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ReminderCronExpression other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ExpressionText);

    private static CronFormat DetectFormat(string expression) => ReminderCronParser.DetectFormat(expression);

    private static void EnsureUtc(DateTime value, string argumentName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("DateTime must use DateTimeKind.Utc.", argumentName);
        }
    }
}
