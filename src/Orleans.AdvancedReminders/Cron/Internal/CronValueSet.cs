using System.Globalization;
using System.Numerics;

namespace Orleans.AdvancedReminders.Cron.Internal;

/// <summary>A bounded cron field stored without per-value allocations.</summary>
internal readonly record struct CronValueSet(ulong Bits)
{
    public bool Contains(int value) => (Bits & (1UL << value)) != 0;

    public int Next(int minimum)
    {
        var remaining = minimum >= 64 ? 0 : Bits & (ulong.MaxValue << minimum);
        return remaining == 0 ? -1 : BitOperations.TrailingZeroCount(remaining);
    }

    public static CronValueSet Parse(string text, int minimum, int maximum, string[]? names = null, bool sundayAlias = false)
    {
        if (text is "*" or "?")
        {
            var last = sundayAlias ? 6 : maximum;
            return new CronValueSet(((1UL << (last + 1)) - 1) & (ulong.MaxValue << minimum));
        }

        ulong selected = 0;
        var rest = text.AsSpan();
        while (true)
        {
            var comma = rest.IndexOf(',');
            var item = comma < 0 ? rest : rest[..comma];
            var slash = item.IndexOf('/');
            var range = slash < 0 ? item : item[..slash];
            var step = slash < 0 ? 1 : ReadNumber(item[(slash + 1)..], 1, sundayAlias ? 7 : maximum - minimum + 1);
            var dash = range.IndexOf('-');
            int start, end;
            if (range is "*" or "?")
            {
                start = minimum;
                end = maximum;
            }
            else if (dash < 0)
            {
                start = ReadValue(range, minimum, maximum, names);
                end = slash < 0 ? start : maximum;
            }
            else
            {
                start = ReadValue(range[..dash], minimum, maximum, names);
                end = ReadValue(range[(dash + 1)..], minimum, maximum, names);
            }

            var width = sundayAlias ? 7 : maximum - minimum + 1;
            var distance = sundayAlias && end - start == 7 ? 6 : (end - start + width) % width;
            for (var offset = 0; offset <= distance; offset += step)
            {
                var value = minimum + (start - minimum + offset) % width;
                selected |= 1UL << value;
            }

            if (comma < 0)
            {
                return new CronValueSet(selected);
            }

            rest = rest[(comma + 1)..];
        }
    }

    internal static int ReadValue(ReadOnlySpan<char> text, int minimum, int maximum, string[]? names)
    {
        if (names is not null)
        {
            for (var index = 0; index < names.Length; index++)
            {
                if (text.Equals(names[index], StringComparison.OrdinalIgnoreCase))
                {
                    return minimum + index;
                }
            }
        }

        return ReadNumber(text, minimum, maximum);
    }

    internal static int ReadNumber(ReadOnlySpan<char> text, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < minimum || value > maximum)
        {
            throw Invalid(text);
        }

        return value;
    }

    internal static ReminderCronParseException Invalid(ReadOnlySpan<char> text) => new($"Invalid cron field '{text.ToString()}'.");
}
