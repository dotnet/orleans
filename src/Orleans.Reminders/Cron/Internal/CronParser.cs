#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Orleans.Reminders.Cron.Internal
{
    internal static class CronParser
    {
        private const int MinNthDayOfWeek = 1;
        private const int MaxNthDayOfWeek = 5;
        private const int SundayBits = 0b1000_0001;

        public static unsafe CronExpression Parse(string expression, CronFormat format)
        {
            fixed (char* value = expression)
            {
                var pointer = value;

                SkipWhiteSpaces(ref pointer);

                if (Accept(ref pointer, '@'))
                {
                    var cronExpression = ParseMacro(ref pointer);
                    SkipWhiteSpaces(ref pointer);

                    if (ReferenceEquals(cronExpression, null) || !IsEndOfString(*pointer)) ThrowFormatException("Macro: Unexpected character '{0}' on position {1}.", *pointer, pointer - value);
                    return cronExpression;
                }

                ulong  second = default;
                byte  nthDayOfWeek = default;
                byte  lastMonthOffset = default;

                CronExpressionFlag flags = default;

                if (format == CronFormat.IncludeSeconds)
                {
                    second = ParseField(CronField.Seconds, ref pointer, ref flags);
                    ParseWhiteSpace(CronField.Seconds, ref pointer);
                }
                else
                {
                    SetBit(ref second, CronField.Seconds.First);
                }

                var minute = ParseField(CronField.Minutes, ref pointer, ref flags);
                ParseWhiteSpace(CronField.Minutes, ref pointer);

                var hour = (uint)ParseField(CronField.Hours, ref pointer, ref flags);
                ParseWhiteSpace(CronField.Hours, ref pointer);

                var dayOfMonth = (uint)ParseDayOfMonth(ref pointer, ref flags, ref lastMonthOffset);

                ParseWhiteSpace(CronField.DaysOfMonth, ref pointer);

                var month = (ushort)ParseField(CronField.Months, ref pointer, ref flags);
                ParseWhiteSpace(CronField.Months, ref pointer);

                var dayOfWeek = (byte)ParseDayOfWeek(ref pointer, ref flags, ref nthDayOfWeek);
                ParseEndOfString(ref pointer);

                // Make sundays equivalent.
                if ((dayOfWeek & SundayBits) != 0)
                {
                    dayOfWeek |= SundayBits;
                }

                return new CronExpression(
                    second,
                    minute,
                    hour,
                    dayOfMonth,
                    month,
                    dayOfWeek,
                    nthDayOfWeek,
                    lastMonthOffset,
                    flags);
            }
        }

        private static unsafe void SkipWhiteSpaces(ref char* pointer)
        {
            while (IsWhiteSpace(*pointer)) { pointer++; }
        }

        private static unsafe void ParseWhiteSpace(CronField prevField, ref char* pointer)
        {
            if (!IsWhiteSpace(*pointer)) ThrowFormatException(prevField, "Unexpected character '{0}'.", *pointer);
            SkipWhiteSpaces(ref pointer);
        }

        private static unsafe void ParseEndOfString(ref char* pointer)
        {
            if (!IsWhiteSpace(*pointer) && !IsEndOfString(*pointer)) ThrowFormatException(CronField.DaysOfWeek, "Unexpected character '{0}'.", *pointer);

            SkipWhiteSpaces(ref pointer);
            if (!IsEndOfString(*pointer)) ThrowFormatException("Unexpected character '{0}'.", *pointer);
        }

        [SuppressMessage("SonarLint", "S1764:IdenticalExpressionsShouldNotBeUsedOnBothSidesOfOperators", Justification = "Expected, as the AcceptCharacter method produces side effects.")]
        private static unsafe CronExpression? ParseMacro(ref char* pointer)
        {
            switch (ToUpper(*pointer++))
            {
                case 'A':
                    if (AcceptCharacter(ref pointer, 'N') &&
                        AcceptCharacter(ref pointer, 'N') &&
                        AcceptCharacter(ref pointer, 'U') &&
                        AcceptCharacter(ref pointer, 'A') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Yearly;
                    return null;
                case 'D':
                    if (AcceptCharacter(ref pointer, 'A') &&
                        AcceptCharacter(ref pointer, 'I') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Daily;
                    return null;
                case 'E':
                    if (AcceptCharacter(ref pointer, 'V') &&
                        AcceptCharacter(ref pointer, 'E') &&
                        AcceptCharacter(ref pointer, 'R') &&
                        AcceptCharacter(ref pointer, 'Y') &&
                        Accept(ref pointer, '_'))
                    {
                        if (AcceptCharacter(ref pointer, 'M') &&
                            AcceptCharacter(ref pointer, 'I') &&
                            AcceptCharacter(ref pointer, 'N') &&
                            AcceptCharacter(ref pointer, 'U') &&
                            AcceptCharacter(ref pointer, 'T') &&
                            AcceptCharacter(ref pointer, 'E'))
                            return CronExpression.EveryMinute;

                        if (*(pointer - 1) != '_') return null;

                        if (AcceptCharacter(ref pointer, 'S') &&
                            AcceptCharacter(ref pointer, 'E') &&
                            AcceptCharacter(ref pointer, 'C') &&
                            AcceptCharacter(ref pointer, 'O') &&
                            AcceptCharacter(ref pointer, 'N') &&
                            AcceptCharacter(ref pointer, 'D'))
                            return CronExpression.EverySecond;
                    }

                    return null;
                case 'H':
                    if (AcceptCharacter(ref pointer, 'O') &&
                        AcceptCharacter(ref pointer, 'U') &&
                        AcceptCharacter(ref pointer, 'R') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Hourly;
                    return null;
                case 'M':
                    if (AcceptCharacter(ref pointer, 'O') &&
                        AcceptCharacter(ref pointer, 'N') &&
                        AcceptCharacter(ref pointer, 'T') &&
                        AcceptCharacter(ref pointer, 'H') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Monthly;

                    if (ToUpper(*(pointer - 1)) == 'M' &&
                        AcceptCharacter(ref pointer, 'I') &&
                        AcceptCharacter(ref pointer, 'D') &&
                        AcceptCharacter(ref pointer, 'N') &&
                        AcceptCharacter(ref pointer, 'I') &&
                        AcceptCharacter(ref pointer, 'G') &&
                        AcceptCharacter(ref pointer, 'H') &&
                        AcceptCharacter(ref pointer, 'T'))
                        return CronExpression.Daily;

                    return null;
                case 'W':
                    if (AcceptCharacter(ref pointer, 'E') &&
                        AcceptCharacter(ref pointer, 'E') &&
                        AcceptCharacter(ref pointer, 'K') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Weekly;
                    return null;
                case 'Y':
                    if (AcceptCharacter(ref pointer, 'E') &&
                        AcceptCharacter(ref pointer, 'A') &&
                        AcceptCharacter(ref pointer, 'R') &&
                        AcceptCharacter(ref pointer, 'L') &&
                        AcceptCharacter(ref pointer, 'Y'))
                        return CronExpression.Yearly;
                    return null;
                default:
                    pointer--;
                    return null;
            }
        }

        private static unsafe ulong ParseField(CronField field, ref char* pointer, ref CronExpressionFlag flags)
        {
            if (Accept(ref pointer, '*') || Accept(ref pointer, '?'))
            {
                if (field.CanDefineInterval) flags |= CronExpressionFlag.Interval;
                return ParseStar(field, ref pointer);
            }

            var num = ParseValue(field, ref pointer);

            var bits = ParseRange(field, ref pointer, num, ref flags);
            if (Accept(ref pointer, ',')) bits |= ParseList(field, ref pointer, ref flags);

            return bits;
        }

        private static unsafe ulong ParseDayOfMonth(ref char* pointer, ref CronExpressionFlag flags, ref byte lastDayOffset)
        {
            var field = CronField.DaysOfMonth;

            if (Accept(ref pointer, '*') || Accept(ref pointer, '?')) return ParseStar(field, ref pointer);

            if (AcceptCharacter(ref pointer, 'L')) return ParseLastDayOfMonth(field, ref pointer, ref flags, ref lastDayOffset);

            var dayOfMonth = ParseValue(field, ref pointer);

            if (AcceptCharacter(ref pointer, 'W'))
            {
                flags |= CronExpressionFlag.NearestWeekday;
                return GetBit(dayOfMonth);
            }

            var bits = ParseRange(field, ref pointer, dayOfMonth, ref flags);
            if (Accept(ref pointer, ',')) bits |= ParseList(field, ref pointer, ref flags);

            return bits;
        }

        private static unsafe ulong ParseDayOfWeek(ref char* pointer, ref CronExpressionFlag flags, ref byte nthWeekDay)
        {
            var field = CronField.DaysOfWeek;
            if (Accept(ref pointer, '*') || Accept(ref pointer, '?')) return ParseStar(field, ref pointer);

            var dayOfWeek = ParseValue(field, ref pointer);

            if (AcceptCharacter(ref pointer, 'L')) return ParseLastWeekDay(dayOfWeek, ref flags);
            if (Accept(ref pointer, '#')) return ParseNthWeekDay(field, ref pointer, dayOfWeek, ref flags, out nthWeekDay);

            var bits = ParseRange(field, ref pointer, dayOfWeek, ref flags);
            if (Accept(ref pointer, ',')) bits |= ParseList(field, ref pointer, ref flags);

            return bits;
        }

        private static unsafe ulong ParseStar(CronField field, ref char* pointer)
        {
            return Accept(ref pointer, '/')
                ? ParseStep(field, ref pointer, field.First, field.Last)
                : field.AllBits;
        }

        private static unsafe ulong ParseList(CronField field, ref char* pointer, ref CronExpressionFlag flags)
        {
            var num = ParseValue(field, ref pointer);
            var bits = ParseRange(field, ref pointer, num, ref flags);

            do
            {
                if (!Accept(ref pointer, ',')) return bits;

                bits |= ParseList(field, ref pointer, ref flags);
            } while (true);
        }

        private static unsafe ulong ParseRange(CronField field, ref char* pointer, int low, ref CronExpressionFlag flags)
        {
            if (!Accept(ref pointer, '-'))
            {
                if (!Accept(ref pointer, '/')) return GetBit(low);

                if (field.CanDefineInterval) flags |= CronExpressionFlag.Interval;
                return ParseStep(field, ref pointer, low, field.Last);
            }

            if (field.CanDefineInterval) flags |= CronExpressionFlag.Interval;

            var high = ParseValue(field, ref pointer);
            if (Accept(ref pointer, '/')) return ParseStep(field, ref pointer, low, high);
            return GetBits(field, low, high, 1);
        }

        private static unsafe ulong ParseStep(CronField field, ref char* pointer, int low, int high)
        {
            // Get the step size -- note: we don't pass the
            // names here, because the number is not an
            // element id, it's a step size.  'low' is
            // sent as a 0 since there is no offset either.
            var step = ParseNumber(field, ref pointer, 1, field.Last);
            return GetBits(field, low, high, step);
        }

        private static unsafe ulong ParseLastDayOfMonth(CronField field, ref char* pointer, ref CronExpressionFlag flags, ref byte lastMonthOffset)
        {
            flags |= CronExpressionFlag.DayOfMonthLast;

            if (Accept(ref pointer, '-')) lastMonthOffset = (byte)ParseNumber(field, ref pointer, 0, field.Last - 1);
            if (AcceptCharacter(ref pointer, 'W')) flags |= CronExpressionFlag.NearestWeekday;
            return field.AllBits;
        }

        private static unsafe ulong ParseNthWeekDay(CronField field, ref char* pointer, int dayOfWeek, ref CronExpressionFlag flags, out byte nthDayOfWeek)
        {
            nthDayOfWeek = (byte)ParseNumber(field, ref pointer, MinNthDayOfWeek, MaxNthDayOfWeek);
            flags |= CronExpressionFlag.NthDayOfWeek;
            return GetBit(dayOfWeek);
        }

        private static ulong ParseLastWeekDay(int dayOfWeek, ref CronExpressionFlag flags)
        {
            flags |= CronExpressionFlag.DayOfWeekLast;
            return GetBit(dayOfWeek);
        }

        private static unsafe bool Accept(ref char* pointer, char character)
        {
            if (*pointer == character)
            {
                pointer++;
                return true;
            }

            return false;
        }

        private static unsafe bool AcceptCharacter(ref char* pointer, char character)
        {
            if (ToUpper(*pointer) == character)
            {
                pointer++;
                return true;
            }

            return false;
        }

        private static unsafe int ParseNumber(CronField field, ref char* pointer, int low, int high)
        {
            var num = GetNumber(ref pointer, null);
            if (num == -1 || num < low || num > high)
            {
                ThrowFormatException(field, "Value must be a number between {0} and {1} (all inclusive).", low, high);
            }
            return num;
        }

        private static unsafe int ParseValue(CronField field, ref char* pointer)
        {
            var num = GetNumber(ref pointer, field.Names);
            if (num == -1 || num < field.First || num > field.Last)
            {
                ThrowFormatException(field, "Value must be a number between {0} and {1} (all inclusive).", field.First, field.Last);
            }
            return num;
        }

        private static ulong GetBits(CronField field, int num1, int num2, int step)
        {
            if (num2 < num1) return GetReversedRangeBits(field, num1, num2, step);
            if (step == 1) return (1UL << (num2 + 1)) - (1UL << num1);

            return GetRangeBits(num1, num2, step);
        }

        private static ulong GetRangeBits(int low, int high, int step)
        {
            var bits = 0UL;
            for (var i = low; i <= high; i += step)
            {
                SetBit(ref bits, i);
            }
            return bits;
        }

        private static ulong GetReversedRangeBits(CronField field, int num1, int num2, int step)
        {
            var high = field.Last;
            // Skip one of sundays.
            if (field == CronField.DaysOfWeek) high--;

            var bits = GetRangeBits(num1, high, step);
            
            num1 = field.First + step - (high - num1) % step - 1;
            return bits | GetRangeBits(num1, num2, step);
        }

        private static ulong GetBit(int num1)
        {
            return 1UL << num1;
        }

        private static unsafe int GetNumber(ref char* pointer, int[]? names)
        {
            if (IsDigit(*pointer))
            {
                var num = GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return num;

                num = num * 10 + GetNumeric(*pointer++);

                if (!IsDigit(*pointer)) return num;
                return -1;
            }

            if (names == null) return -1;

            if (!IsLetter(*pointer)) return -1;
            var buffer = ToUpper(*pointer++);

            if (!IsLetter(*pointer)) return -1;
            buffer |= ToUpper(*pointer++) << 8;

            if (!IsLetter(*pointer)) return -1;
            buffer |= ToUpper(*pointer++) << 16;

            var length = names.Length;

            for (var i = 0; i < length; i++)
            {
                if (buffer == names[i])
                {
                    return i;
                }
            }

            return -1;
        }

        private static void SetBit(ref ulong value, int index)
        {
            value |= 1UL << index;
        }

        private static bool IsEndOfString(int code)
        {
            return code == '\0';
        }

        private static bool IsWhiteSpace(int code)
        {
            return code == '\t' || code == ' ';
        }

        private static bool IsDigit(int code)
        {
            return code >= 48 && code <= 57;
        }

        private static bool IsLetter(int code)
        {
            return (code >= 65 && code <= 90) || (code >= 97 && code <= 122);
        }

        private static int GetNumeric(int code)
        {
            return code - 48;
        }

        private static uint ToUpper(uint code)
        {
            if (code >= 97 && code <= 122)
            {
                return code - 32;
            }

            return code;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [DoesNotReturn]
        private static void ThrowFormatException(CronField field, string format, params object[] args)
        {
            throw new CronFormatException($"{CronFormatException.BaseMessage} {field}: {String.Format(CultureInfo.CurrentCulture, format, args)}");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [DoesNotReturn]
        private static void ThrowFormatException(string format, params object[] args)
        {
            throw new CronFormatException($"{CronFormatException.BaseMessage} {String.Format(CultureInfo.CurrentCulture, format, args)}");
        }
    }
}