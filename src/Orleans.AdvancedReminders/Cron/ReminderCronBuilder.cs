#nullable enable
using Orleans.AdvancedReminders.Cron.Internal;

namespace Orleans.AdvancedReminders;


/// <summary>
/// Provides typed helpers for building reminder cron expressions.
/// </summary>
public sealed partial class ReminderCronBuilder
{
    private readonly string _expression;
    private readonly TimeZoneInfo _timeZone;
    private readonly Lazy<ReminderCronExpression> _parsedExpression;

    private ReminderCronBuilder(string expression, TimeZoneInfo timeZone, Lazy<ReminderCronExpression>? parsedExpression = null)
    {
        _expression = expression;
        _timeZone = timeZone;
        _parsedExpression = parsedExpression ?? new Lazy<ReminderCronExpression>(() => ReminderCronExpression.Parse(expression));
    }

    /// <summary>
    /// Uses a raw cron expression string.
    /// </summary>
    public static ReminderCronBuilder FromExpression(string expression)
        => FromExpression(expression, TimeZoneInfo.Utc);

    /// <summary>
    /// Uses a raw cron expression string and scheduling time zone.
    /// </summary>
    public static ReminderCronBuilder FromExpression(string expression, TimeZoneInfo? timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new ReminderCronBuilder(expression.Trim(), timeZone ?? TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Builds a standard five-field cron expression from strongly typed fields.
    /// </summary>
    public static ReminderCronBuilder FromFields(
        ReminderCronMinute minute,
        ReminderCronHour hour,
        ReminderCronDayOfMonth dayOfMonth,
        ReminderCronMonth month,
        ReminderCronDayOfWeek dayOfWeek)
    {
        ArgumentNullException.ThrowIfNull(minute);
        ArgumentNullException.ThrowIfNull(hour);
        ArgumentNullException.ThrowIfNull(dayOfMonth);
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(dayOfWeek);

        return new ReminderCronBuilder(
            $"{minute.Expression.Text} {hour.Expression.Text} {dayOfMonth.Expression.Text} {month.Expression.Text} {dayOfWeek.Expression.Text}",
            TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Builds a six-field cron expression, including seconds, from strongly typed fields.
    /// </summary>
    public static ReminderCronBuilder FromFields(
        ReminderCronSecond second,
        ReminderCronMinute minute,
        ReminderCronHour hour,
        ReminderCronDayOfMonth dayOfMonth,
        ReminderCronMonth month,
        ReminderCronDayOfWeek dayOfWeek)
    {
        ArgumentNullException.ThrowIfNull(second);
        var schedule = FromFields(minute, hour, dayOfMonth, month, dayOfWeek);
        return new ReminderCronBuilder($"{second.Expression.Text} {schedule._expression}", TimeZoneInfo.Utc);
    }

    /// <summary>
    /// Returns a copy of this builder configured to evaluate occurrences in the provided time zone.
    /// </summary>
    public ReminderCronBuilder InTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return new ReminderCronBuilder(_expression, timeZone, _parsedExpression);
    }

    /// <summary>
    /// Returns a copy of this builder configured to evaluate occurrences in the provided time zone id.
    /// </summary>
    public ReminderCronBuilder InTimeZone(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        var zone = ReminderCronTimeZoneResolver.Resolve(timeZoneId);
        return InTimeZone(zone);
    }

    /// <summary>
    /// Gets the time zone used by this builder for occurrence calculations.
    /// </summary>
    public TimeZoneInfo TimeZone => _timeZone;

    /// <summary>
    /// Gets the next occurrence in UTC, evaluated using this builder's time zone.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
    {
        var expression = _parsedExpression.Value;
        return ReminderCronTimeZoneResolver.IsUtc(_timeZone)
            ? expression.GetNextOccurrence(fromUtc, inclusive)
            : expression.GetNextOccurrence(fromUtc, _timeZone, inclusive);
    }

    /// <summary>
    /// Gets the next occurrence as a UTC timestamp using this builder's scheduling time zone.
    /// The input offset identifies the starting instant and does not replace that time zone.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from, bool inclusive = false)
        => GetNextOccurrence(from.UtcDateTime, inclusive) is { } occurrence ? new DateTimeOffset(occurrence) : null;

    /// <summary>
    /// Gets all occurrences in UTC for the provided range, evaluated using this builder's time zone.
    /// </summary>
    public IEnumerable<DateTime> GetOccurrences(
        DateTime fromUtc,
        DateTime toUtc,
        bool fromInclusive = true,
        bool toInclusive = false)
    {
        var expression = _parsedExpression.Value;
        return ReminderCronTimeZoneResolver.IsUtc(_timeZone)
            ? expression.GetOccurrences(fromUtc, toUtc, fromInclusive, toInclusive)
            : expression.GetOccurrences(fromUtc, toUtc, _timeZone, fromInclusive, toInclusive);
    }

    /// <summary>
    /// Gets occurrences as UTC timestamps between two instants using this builder's scheduling time zone.
    /// The range endpoints may have different offsets.
    /// </summary>
    public IEnumerable<DateTimeOffset> GetOccurrences(DateTimeOffset from, DateTimeOffset to, bool fromInclusive = true, bool toInclusive = false)
        => ReminderCronExpression.ToDateTimeOffsets(GetOccurrences(from.UtcDateTime, to.UtcDateTime, fromInclusive, toInclusive));

    /// <summary>
    /// Returns the resulting cron expression string.
    /// </summary>
    public string ToExpressionString() => _expression;

    /// <summary>
    /// Parses and validates the builder output as a typed cron expression.
    /// </summary>
    public ReminderCronExpression ToCronExpression() => _parsedExpression.Value;

    /// <summary>
    /// Alias for <see cref="ToCronExpression"/>.
    /// </summary>
    public ReminderCronExpression Build() => ToCronExpression();
}
