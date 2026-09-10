using System.Globalization;
using System.Text;

using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.AdvancedReminders.Redis;

internal partial class RedisReminderTable
{
    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        try
        {
            var (from, to) = GetFilter(grainId, reminderName);
            RedisValue[] values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
            if (values.Length == 0)
            {
                return null;
            }
            else
            {
                return ConvertToEntry(values.Single());
            }
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task<ReminderTableData> ReadRows(GrainId grainId)
    {
        try
        {
            var (from, to) = GetFilter(grainId);
            RedisValue[] values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
            IEnumerable<ReminderEntry> records = values.Select(static v => ConvertToEntry(v));
            return new ReminderTableData(records);
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        try
        {
            var (_, from) = GetFilter(begin);
            var (_, to) = GetFilter(end);
            IEnumerable<RedisValue> values;
            if (begin < end)
            {
                // -----begin******end-----
                values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
            }

            else
            {
                // *****end------begin*****
                RedisValue[] values1 = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, "\"FFFFFFFF\",#");
                RedisValue[] values2 = await _db.SortedSetRangeByValueAsync(_hashSetKey, "\"00000000\",\"", to);
                values = values1.Concat(values2);
            }

            IEnumerable<ReminderEntry> records = values.Select(static v => ConvertToEntry(v));
            return new ReminderTableData(records);
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        try
        {
            var (phase, lastValue) = ParseContinuationToken(continuationToken);
            var (_, beginFrom) = GetFilter(begin);
            var (_, endTo) = GetFilter(end);
            var wraps = begin >= end;
            var result = new List<RedisValue>(maxRows);

            while (result.Count < maxRows)
            {
                RedisValue from;
                RedisValue to;
                var exclude = lastValue is null ? Exclude.None : Exclude.Start;
                if (!wraps)
                {
                    if (phase != 0)
                    {
                        break;
                    }

                    from = lastValue is null ? beginFrom : lastValue;
                    to = endTo;
                }
                else if (phase == 0)
                {
                    from = lastValue is null ? beginFrom : lastValue;
                    to = "\"FFFFFFFF\",#";
                }
                else if (phase == 1)
                {
                    from = lastValue is null ? "\"00000000\",\"" : lastValue;
                    to = endTo;
                }
                else
                {
                    break;
                }

                var remaining = maxRows - result.Count;
                var values = await _db.SortedSetRangeByValueAsync(
                    _hashSetKey,
                    from,
                    to,
                    exclude,
                    Order.Ascending,
                    skip: 0,
                    remaining);
                if (values.Length == remaining)
                {
                    result.AddRange(values);
                    var lastEntry = ConvertToEntry(values[remaining - 1]);
                    var (_, lastKeyBoundary) = GetFilter(lastEntry.GrainId, lastEntry.ReminderName);
                    return new ReminderTableData(
                        result.Select(static value => ConvertToEntry(value)),
                        FormatContinuationToken(phase, lastKeyBoundary.ToString()));
                }

                result.AddRange(values);
                if (!wraps || phase == 1)
                {
                    return new ReminderTableData(result.Select(static value => ConvertToEntry(value)));
                }

                phase = 1;
                lastValue = null;
                if (result.Count == maxRows)
                {
                    return new ReminderTableData(
                        result.Select(static value => ConvertToEntry(value)),
                        FormatContinuationToken(phase, lastValue));
                }
            }

            return new ReminderTableData(result.Select(static value => ConvertToEntry(value)));
        }
        catch (Exception exception) when (exception is not ArgumentException and not ArgumentOutOfRangeException)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    private static (int Phase, string? LastValue) ParseContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return (0, null);
        }

        var separator = continuationToken.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(continuationToken.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var phase)
            || phase is < 0 or > 1)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(continuationToken));
        }

        try
        {
            var encoded = continuationToken[(separator + 1)..];
            return (phase, encoded.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(continuationToken), exception);
        }
    }

    private static string FormatContinuationToken(int phase, string? lastValue)
        => string.Concat(
            phase.ToString(CultureInfo.InvariantCulture),
            ":",
            lastValue is null ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(lastValue)));

    private (RedisValue from, RedisValue to) GetFilter(uint grainHash)
    {
        return GetFilter(grainHash.ToString("X8"));
    }

    private (RedisValue from, RedisValue to) GetFilter(GrainId grainId)
    {
        return GetFilter(grainId.GetUniformHashCode().ToString("X8"), grainId.ToString());
    }

    private (RedisValue from, RedisValue to) GetFilter(GrainId grainId, string reminderName)
    {
        return GetFilter(grainId.GetUniformHashCode().ToString("X8"), grainId.ToString(), reminderName);
    }

    private (RedisValue from, RedisValue to) GetFilter(GrainId grainId, string reminderName, string eTag)
    {
        return GetFilter(grainId.GetUniformHashCode().ToString("X8"), grainId.ToString(), reminderName, eTag);
    }

    private (RedisValue from, RedisValue to) GetFilter(params string[] segments)
    {
        string prefix = SerializeSegments(segments);
        return ($"{prefix},\"", $"{prefix},#");
    }
}
