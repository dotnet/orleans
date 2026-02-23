#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Orleans.Reminders.Cron.Internal
{
    /// <summary>
    /// Provides a parser and scheduler for cron expressions.
    /// </summary>
    internal sealed class CronExpression: IEquatable<CronExpression>
    {
        private const long NotFound = -1;

        /// <summary>
        /// Represents a cron expression that fires on Jan 1st every year at midnight.
        /// Equals to "0 0 1 1 *".
        /// </summary>
        public static readonly CronExpression Yearly = Parse("0 0 1 1 *", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires every Sunday at midnight.
        /// Equals to "0 0 * * 0".
        /// </summary>
        public static readonly CronExpression Weekly = Parse("0 0 * * 0", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires on 1st day of every month at midnight.
        /// Equals to "0 0 1 * *".
        /// </summary>
        public static readonly CronExpression Monthly = Parse("0 0 1 * *", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires every day at midnight.
        /// Equals to "0 0 * * *".
        /// </summary>
        public static readonly CronExpression Daily = Parse("0 0 * * *", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires every hour at the beginning of the hour.
        /// Equals to "0 * * * *".
        /// </summary>
        public static readonly CronExpression Hourly = Parse("0 * * * *", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires every minute.
        /// Equals to "* * * * *".
        /// </summary>
        public static readonly CronExpression EveryMinute = Parse("* * * * *", CronFormat.Standard);

        /// <summary>
        /// Represents a cron expression that fires every second.
        /// Equals to "* * * * * *". 
        /// </summary>
        public static readonly CronExpression EverySecond = Parse("* * * * * *", CronFormat.IncludeSeconds);

        private static readonly TimeZoneInfo UtcTimeZone = TimeZoneInfo.Utc;

        private readonly ulong  _second;     // 60 bits -> from 0 bit to 59 bit
        private readonly ulong  _minute;     // 60 bits -> from 0 bit to 59 bit
        private readonly uint   _hour;       // 24 bits -> from 0 bit to 23 bit
        private readonly uint   _dayOfMonth; // 31 bits -> from 1 bit to 31 bit
        private readonly ushort _month;      // 12 bits -> from 1 bit to 12 bit
        private readonly byte  _dayOfWeek;  // 8 bits  -> from 0 bit to 7 bit

        private readonly byte  _nthDayOfWeek;
        private readonly byte  _lastMonthOffset;

        private readonly CronExpressionFlag _flags;

        internal CronExpression(
            ulong second,
            ulong minute,
            uint hour,
            uint dayOfMonth,
            ushort month,
            byte dayOfWeek,
            byte nthDayOfWeek,
            byte lastMonthOffset,
            CronExpressionFlag flags)
        {
            _second = second;
            _minute = minute;
            _hour = hour;
            _dayOfMonth = dayOfMonth;
            _month = month;
            _dayOfWeek = dayOfWeek;
            _nthDayOfWeek = nthDayOfWeek;
            _lastMonthOffset = lastMonthOffset;
            _flags = flags;
        }

        ///<summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified
        /// cron expression. It's supported expressions consisting of 5 fields:
        /// minute, hour, day of month, month, day of week. 
        /// If you want to parse non-standard cron expressions use <see cref="Parse(string, CronFormat)"/> with specified CronFields argument.
        /// </summary>
        public static CronExpression Parse(string expression)
        {
            return Parse(expression, CronFormat.Standard);
        }

        ///<summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified
        /// cron expression. It's supported expressions consisting of 5 or 6 fields:
        /// second (optional), minute, hour, day of month, month, day of week. 
        /// </summary>
        public static CronExpression Parse(string expression, CronFormat format)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(expression);
#else
            if (expression == null) throw new ArgumentNullException(nameof(expression));
#endif

            return CronParser.Parse(expression, format);
        }

        /// <summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified cron expression with the
        /// <see cref="CronFormat.Standard"/> format.
        /// A return value indicates whether the operation succeeded.
        /// </summary>
        public static bool TryParse(string expression, [MaybeNullWhen(returnValue: false)] out CronExpression cronExpression)
        {
            return TryParse(expression, CronFormat.Standard, out cronExpression);
        }

        /// <summary>
        /// Constructs a new <see cref="CronExpression"/> based on the specified cron expression with the specified
        /// <paramref name="format"/>.
        /// A return value indicates whether the operation succeeded.
        /// </summary>
        public static bool TryParse(string expression, CronFormat format, [MaybeNullWhen(returnValue: false)] out CronExpression cronExpression)
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(expression);
#else
            if (expression == null) throw new ArgumentNullException(nameof(expression));
#endif

            try
            {
                cronExpression = Parse(expression, format);
                return true;
            }
            catch (CronFormatException)
            {
                cronExpression = null;
                return false;
            }
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="fromUtc"/> (optionally <paramref name="inclusive"/>) in UTC time zone.
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public DateTime? GetNextOccurrence(DateTime fromUtc, bool inclusive = false)
        {
            if (fromUtc.Kind != DateTimeKind.Utc) ThrowWrongDateTimeKindException(nameof(fromUtc));

            var found = FindOccurrence(fromUtc.Ticks, inclusive);
            if (found == NotFound) return null;

            return new DateTime(found, DateTimeKind.Utc);
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="fromUtc"/> (optionally <paramref name="inclusive"/>) in given <paramref name="zone"/>
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public DateTime? GetNextOccurrence(DateTime fromUtc, TimeZoneInfo zone, bool inclusive = false)
        {
            if (fromUtc.Kind != DateTimeKind.Utc) ThrowWrongDateTimeKindException(nameof(fromUtc));
            if (ReferenceEquals(zone, null)) ThrowArgumentNullException(nameof(zone));

            if (ReferenceEquals(zone, UtcTimeZone))
            {
                var found = FindOccurrence(fromUtc.Ticks, inclusive);
                if (found == NotFound) return null;

                return new DateTime(found, DateTimeKind.Utc);
            }

#pragma warning disable CA1062
            var occurrence = GetOccurrenceConsideringTimeZone(fromUtc, zone, inclusive);
#pragma warning restore CA1062

            return occurrence?.UtcDateTime;
        }

        /// <summary>
        /// Calculates next occurrence starting with <paramref name="from"/> (optionally <paramref name="inclusive"/>) in given <paramref name="zone"/>
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public DateTimeOffset? GetNextOccurrence(DateTimeOffset from, TimeZoneInfo zone, bool inclusive = false)
        {
            if (ReferenceEquals(zone, null)) ThrowArgumentNullException(nameof(zone));

            if (ReferenceEquals(zone, UtcTimeZone))
            {
                var found = FindOccurrence(from.UtcTicks, inclusive);
                if (found == NotFound) return null;

                return new DateTimeOffset(found, TimeSpan.Zero);
            }

#pragma warning disable CA1062
            var occurrenceUtc = GetNextOccurrence(from.UtcDateTime, zone, inclusive);
#pragma warning restore CA1062

            if (!occurrenceUtc.HasValue) return null;

            return TimeZoneInfo.ConvertTime(new DateTimeOffset(occurrenceUtc.Value, TimeSpan.Zero), zone);
        }

        /// <summary>
        /// Returns the list of next occurrences within the given date/time range,
        /// including <paramref name="fromUtc"/> and excluding <paramref name="toUtc"/>
        /// by default, and UTC time zone. When none of the occurrences found, an 
        /// empty list is returned.
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public IEnumerable<DateTime> GetOccurrences(
            DateTime fromUtc,
            DateTime toUtc,
            bool fromInclusive = true,
            bool toInclusive = false)
        {
            if (fromUtc > toUtc) ThrowFromShouldBeLessThanToException(nameof(fromUtc), nameof(toUtc));

            for (var occurrence = GetNextOccurrence(fromUtc, fromInclusive);
                occurrence < toUtc || occurrence == toUtc && toInclusive;
                // ReSharper disable once RedundantArgumentDefaultValue
                // ReSharper disable once ArgumentsStyleLiteral
                occurrence = GetNextOccurrence(occurrence.Value, inclusive: false))
            {
                yield return occurrence.Value;
            }
        }

        /// <summary>
        /// Returns the list of next occurrences within the given date/time range, including
        /// <paramref name="fromUtc"/> and excluding <paramref name="toUtc"/> by default, and 
        /// specified time zone. When none of the occurrences found, an empty list is returned.
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public IEnumerable<DateTime> GetOccurrences(
            DateTime fromUtc,
            DateTime toUtc,
            TimeZoneInfo zone,
            bool fromInclusive = true,
            bool toInclusive = false)
        {
            if (fromUtc > toUtc) ThrowFromShouldBeLessThanToException(nameof(fromUtc), nameof(toUtc));

            for (var occurrence = GetNextOccurrence(fromUtc, zone, fromInclusive);
                occurrence < toUtc || occurrence == toUtc && toInclusive;
                // ReSharper disable once RedundantArgumentDefaultValue
                // ReSharper disable once ArgumentsStyleLiteral
                occurrence = GetNextOccurrence(occurrence.Value, zone, inclusive: false))
            {
                yield return occurrence.Value;
            }
        }

        /// <summary>
        /// Returns the list of occurrences within the given date/time offset range,
        /// including <paramref name="from"/> and excluding <paramref name="to"/> by
        /// default. When none of the occurrences found, an empty list is returned.
        /// </summary>
        /// <exception cref="ArgumentException"/>
        public IEnumerable<DateTimeOffset> GetOccurrences(
            DateTimeOffset from,
            DateTimeOffset to,
            TimeZoneInfo zone,
            bool fromInclusive = true,
            bool toInclusive = false)
        {
            if (from > to) ThrowFromShouldBeLessThanToException(nameof(from), nameof(to));

            for (var occurrence = GetNextOccurrence(from, zone, fromInclusive);
                occurrence < to || occurrence == to && toInclusive;
                // ReSharper disable once RedundantArgumentDefaultValue
                // ReSharper disable once ArgumentsStyleLiteral
                occurrence = GetNextOccurrence(occurrence.Value, zone, inclusive: false))
            {
                yield return occurrence.Value;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var expressionBuilder = new StringBuilder();

            if (_second != 1UL)
            {
                AppendFieldValue(expressionBuilder, CronField.Seconds, _second).Append(' ');
            }

            AppendFieldValue(expressionBuilder, CronField.Minutes, _minute).Append(' ');
            AppendFieldValue(expressionBuilder, CronField.Hours, _hour).Append(' ');
            AppendDayOfMonth(expressionBuilder, _dayOfMonth).Append(' ');
            AppendFieldValue(expressionBuilder, CronField.Months, _month).Append(' ');
            AppendDayOfWeek(expressionBuilder, _dayOfWeek);

            return expressionBuilder.ToString();
        }

        /// <summary>
        /// Determines whether the specified <see cref="Object"/> is equal to the current <see cref="Object"/>.
        /// </summary>
        /// <param name="other">The <see cref="Object"/> to compare with the current <see cref="Object"/>.</param>
        /// <returns>
        /// <c>true</c> if the specified <see cref="Object"/> is equal to the current <see cref="Object"/>; otherwise, <c>false</c>.
        /// </returns>
        public bool Equals(CronExpression? other)
        {
            if (ReferenceEquals(other, null)) return false;

            return _second == other._second &&
                   _minute == other._minute &&
                   _hour == other._hour &&
                   _dayOfMonth == other._dayOfMonth &&
                   _month == other._month &&
                   _dayOfWeek == other._dayOfWeek &&
                   _nthDayOfWeek == other._nthDayOfWeek &&
                   _lastMonthOffset == other._lastMonthOffset &&
                   _flags == other._flags;
        }

        /// <summary>
        /// Determines whether the specified <see cref="System.Object" /> is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="System.Object" /> to compare with this instance.</param>
        /// <returns>
        /// <c>true</c> if the specified <see cref="System.Object" /> is equal to this instance;
        /// otherwise, <c>false</c>.
        /// </returns>
        public override bool Equals(object? obj) => Equals(obj as CronExpression);

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        /// <returns>
        /// A hash code for this instance, suitable for use in hashing algorithms and data
        /// structures like a hash table. 
        /// </returns>
        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = _second.GetHashCode();
                hashCode = (hashCode * 397) ^ _minute.GetHashCode();
                hashCode = (hashCode * 397) ^ (int)_hour;
                hashCode = (hashCode * 397) ^ (int)_dayOfMonth;
                hashCode = (hashCode * 397) ^ (int)_month;
                hashCode = (hashCode * 397) ^ (int)_dayOfWeek;
                hashCode = (hashCode * 397) ^ (int)_nthDayOfWeek;
                hashCode = (hashCode * 397) ^ _lastMonthOffset;
                hashCode = (hashCode * 397) ^ (int)_flags;

                return hashCode;
            }
        }

        /// <summary>
        /// Implements the operator ==.
        /// </summary>
        public static bool operator ==(CronExpression? left, CronExpression? right) => Equals(left, right);

        /// <summary>
        /// Implements the operator !=.
        /// </summary>
        public static bool operator !=(CronExpression? left, CronExpression? right) => !Equals(left, right);

        private DateTimeOffset? GetOccurrenceConsideringTimeZone(DateTime fromUtc, TimeZoneInfo zone, bool inclusive)
        {
            if (!DateTimeHelper.IsRound(fromUtc))
            {
                // Rarely, if fromUtc is very close to DST transition, `TimeZoneInfo.ConvertTime` may not convert it correctly on Windows.
                // E.g., In Jordan Time DST started 2017-03-31 00:00 local time. Clocks jump forward from `2017-03-31 00:00 +02:00` to `2017-03-31 01:00 +3:00`.
                // But `2017-03-30 23:59:59.9999000 +02:00` will be converted to `2017-03-31 00:59:59.9999000 +03:00` instead of `2017-03-30 23:59:59.9999000 +02:00` on Windows.
                // It can lead to skipped occurrences. To avoid such errors we floor fromUtc to seconds:
                // `2017-03-30 23:59:59.9999000 +02:00` will be floored to `2017-03-30 23:59:59.0000000 +02:00` and will be converted to `2017-03-30 23:59:59.0000000 +02:00`.
                fromUtc = DateTimeHelper.FloorToSeconds(fromUtc);
                inclusive = false;
            }

            var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone);

            if (TimeZoneHelper.IsAmbiguousTime(zone, fromLocal))
            {
                var currentOffset = zone.GetUtcOffset(fromUtc);
                var standardOffset = zone.GetUtcOffset(fromLocal);
               
                if (standardOffset != currentOffset)
                {
                    var daylightOffset = TimeZoneHelper.GetDaylightOffset(zone, fromLocal);
                    var daylightTimeLocalEnd = TimeZoneHelper.GetDaylightTimeEnd(zone, fromLocal, daylightOffset).DateTime;

                    // Early period, try to find anything here.
                    var foundInDaylightOffset = FindOccurrence(fromLocal.Ticks, daylightTimeLocalEnd.Ticks, inclusive);
                    if (foundInDaylightOffset != NotFound) return new DateTimeOffset(foundInDaylightOffset, daylightOffset);

                    fromLocal = TimeZoneHelper.GetStandardTimeStart(zone, fromLocal, daylightOffset).DateTime;
                    inclusive = true;
                }

                // Skip late ambiguous interval.
                var ambiguousIntervalLocalEnd = TimeZoneHelper.GetAmbiguousIntervalEnd(zone, fromLocal).DateTime;

                if (HasFlag(CronExpressionFlag.Interval))
                {
                    var foundInStandardOffset = FindOccurrence(fromLocal.Ticks, ambiguousIntervalLocalEnd.Ticks - 1, inclusive);
                    if (foundInStandardOffset != NotFound) return new DateTimeOffset(foundInStandardOffset, standardOffset);
                }

                fromLocal = ambiguousIntervalLocalEnd;
                inclusive = true;
            }

            var occurrenceTicks = FindOccurrence(fromLocal.Ticks, inclusive);
            if (occurrenceTicks == NotFound) return null;

            var occurrence = new DateTime(occurrenceTicks, DateTimeKind.Unspecified);

            if (zone.IsInvalidTime(occurrence))
            {
                var nextValidTime = TimeZoneHelper.GetDaylightTimeStart(zone, occurrence);
                return nextValidTime;
            }

            if (TimeZoneHelper.IsAmbiguousTime(zone, occurrence))
            {
                var daylightOffset = TimeZoneHelper.GetDaylightOffset(zone, occurrence);
                return new DateTimeOffset(occurrence, daylightOffset);
            }

            return new DateTimeOffset(occurrence, zone.GetUtcOffset(occurrence));
        }

        private long FindOccurrence(long startTimeTicks, long endTimeTicks, bool startInclusive)
        {
            var found = FindOccurrence(startTimeTicks, startInclusive);

            if (found == NotFound || found > endTimeTicks) return NotFound;
            return found;
        }

        private long FindOccurrence(long ticks, bool startInclusive)
        {
            if (!startInclusive) ticks++;

            CalendarHelper.FillDateTimeParts(
                ticks,
                out int startSecond,
                out int startMinute,
                out int startHour,
                out int startDay,
                out int startMonth,
                out int startYear);

            var minMatchedDay = GetFirstSet(_dayOfMonth);

            var second = startSecond;
            var minute = startMinute;
            var hour = startHour;
            var day = startDay;
            var month = startMonth;
            var year = startYear;

            if (!GetBit(_second, second) && !Move(_second, ref second)) minute++;
            if (!GetBit(_minute, minute) && !Move(_minute, ref minute)) hour++;
            if (!GetBit(_hour, hour) && !Move(_hour, ref hour)) day++;

            // If NearestWeekday flag is set it's possible forward shift.
            if (HasFlag(CronExpressionFlag.NearestWeekday)) day = CronField.DaysOfMonth.First;

            if (!GetBit(_dayOfMonth, day) && !Move(_dayOfMonth, ref day)) goto RetryMonth;
            if (!GetBit(_month, month)) goto RetryMonth;

            Retry:

            if (day > GetLastDayOfMonth(year, month)) goto RetryMonth;

            if (HasFlag(CronExpressionFlag.DayOfMonthLast)) day = GetLastDayOfMonth(year, month);

            var lastCheckedDay = day;

            if (HasFlag(CronExpressionFlag.NearestWeekday)) day = CalendarHelper.MoveToNearestWeekDay(year, month, day);

            if (IsDayOfWeekMatch(year, month, day))
            {
                if (CalendarHelper.IsGreaterThan(year, month, day, startYear, startMonth, startDay)) goto RolloverDay;
                if (hour > startHour) goto RolloverHour;
                if (minute > startMinute) goto RolloverMinute;
                goto ReturnResult;

                RolloverDay: hour = GetFirstSet(_hour);
                RolloverHour: minute = GetFirstSet(_minute);
                RolloverMinute: second = GetFirstSet(_second);

                ReturnResult:

                var found = CalendarHelper.DateTimeToTicks(year, month, day, hour, minute, second);
                if (found >= ticks) return found;
            }

            day = lastCheckedDay;
            if (Move(_dayOfMonth, ref day)) goto Retry;

            RetryMonth:

            if (!Move(_month, ref month))
            {
                year++;
                if (year > DateTime.MaxValue.Year)
                {
                    return NotFound;
                }
            }
            
            day = minMatchedDay;

            goto Retry;
        }

        private static bool Move(ulong fieldBits, ref int fieldValue)
        {
            if (fieldBits >> ++fieldValue == 0)
            {
                fieldValue = GetFirstSet(fieldBits);
                return false;
            }

            fieldValue += GetFirstSet(fieldBits >> fieldValue);
            return true;
        }

        private int GetLastDayOfMonth(int year, int month)
        {
            return CalendarHelper.GetDaysInMonth(year, month) - _lastMonthOffset;
        }

        private bool IsDayOfWeekMatch(int year, int month, int day)
        {
            if (HasFlag(CronExpressionFlag.DayOfWeekLast) && !CalendarHelper.IsLastDayOfWeek(year, month, day) ||
                HasFlag(CronExpressionFlag.NthDayOfWeek) && !CalendarHelper.IsNthDayOfWeek(day, _nthDayOfWeek))
            {
                return false;
            }

            if (_dayOfWeek == CronField.DaysOfWeek.AllBits) return true;

            var dayOfWeek = CalendarHelper.GetDayOfWeek(year, month, day);

            return ((_dayOfWeek >> (int)dayOfWeek) & 1) != 0;
        }

        private static int GetFirstSet(ulong value)
        {
            if (value == 0) return 0;
            return BitOperations.TrailingZeroCount(value);
        }

        private bool HasFlag(CronExpressionFlag value)
        {
            return (_flags & value) != 0;
        }

        private static StringBuilder AppendFieldValue(StringBuilder expressionBuilder, CronField field, ulong fieldValue)
        {
            if (field.AllBits == fieldValue) return expressionBuilder.Append('*');

            // Unset 7 bit for Day of week field because both 0 and 7 stand for Sunday.
            if (field == CronField.DaysOfWeek) fieldValue &= ~(1U << field.Last);

            for (var i = GetFirstSet(fieldValue);; i = GetFirstSet(fieldValue >> i << i))
            {
                expressionBuilder.Append(i);
                if (fieldValue >> ++i == 0) break;
                expressionBuilder.Append(',');
            }

            return expressionBuilder;
        }

        private StringBuilder AppendDayOfMonth(StringBuilder expressionBuilder, uint domValue)
        {
            if (HasFlag(CronExpressionFlag.DayOfMonthLast))
            {
                expressionBuilder.Append('L');
                if (_lastMonthOffset != 0) expressionBuilder.Append(String.Format(CultureInfo.InvariantCulture, "-{0}", _lastMonthOffset));
            }
            else
            {
                AppendFieldValue(expressionBuilder, CronField.DaysOfMonth, (uint)domValue);
            }

            if (HasFlag(CronExpressionFlag.NearestWeekday)) expressionBuilder.Append('W');

            return expressionBuilder;
        }

        private void AppendDayOfWeek(StringBuilder expressionBuilder, uint dowValue)
        {
            AppendFieldValue(expressionBuilder, CronField.DaysOfWeek, dowValue);

            if (HasFlag(CronExpressionFlag.DayOfWeekLast)) expressionBuilder.Append('L');
            else if (HasFlag(CronExpressionFlag.NthDayOfWeek)) expressionBuilder.Append(String.Format(CultureInfo.InvariantCulture, "#{0}", _nthDayOfWeek));
        }

        [DoesNotReturn]
        private static void ThrowFromShouldBeLessThanToException(string fromName, string toName)
        {
            throw new ArgumentException($"The value of the {fromName} argument should be less than the value of the {toName} argument.", fromName);
        }

        [DoesNotReturn]
        private static void ThrowWrongDateTimeKindException(string paramName)
        {
            throw new ArgumentException("The supplied DateTime must have the Kind property set to Utc", paramName);
        }

        [DoesNotReturn]
        private static void ThrowArgumentNullException(string paramName)
        {
            throw new ArgumentNullException(paramName);
        }

        private static bool GetBit(ulong value, int index)
        {
            return (value & (1UL << index)) != 0;
        }
    }
}
