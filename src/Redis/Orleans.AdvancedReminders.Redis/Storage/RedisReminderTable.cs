using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Runtime;

using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.AdvancedReminders.Redis;

internal partial class RedisReminderTable : IReminderTable, IDisposable, IAsyncDisposable
{
    private readonly RedisKey _hashSetKey;
    private readonly RedisReminderTableOptions _redisOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly ILogger _logger;
    private IConnectionMultiplexer _muxer = default!;
    private IDatabase _db = default!;
    private bool _muxerIsShared;

    public RedisReminderTable(
        ILogger<RedisReminderTable> logger,
        IOptions<ClusterOptions> clusterOptions,
        IOptions<RedisReminderTableOptions> redisOptions)
    {
        _redisOptions = redisOptions.Value;
        _clusterOptions = clusterOptions.Value;
        _logger = logger;

        _hashSetKey = Encoding.UTF8.GetBytes($"{_clusterOptions.ServiceId}/advanced-reminders");
    }

    public Task Init() => StartAsync(CancellationToken.None);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>? creationTask = null;
        try
        {
            creationTask = _redisOptions.CreateMultiplexer(_redisOptions);
            (_muxer, _muxerIsShared) = await creationTask.WaitAsync(cancellationToken);
            _db = _muxer.GetDatabase();

            if (_redisOptions.EntryExpiry is { } expiry)
            {
                await _db.KeyExpireAsync(_hashSetKey, expiry).WaitAsync(cancellationToken);
            }
            else
            {
                await _db.KeyPersistAsync(_hashSetKey).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_muxer is not null)
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            else if (creationTask is not null)
            {
                _ = DisposeMultiplexerWhenCreatedAsync(creationTask);
            }

            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                _logger.LogWarning(
                    disposeException,
                    "Error disposing the Redis connection after advanced reminder table initialization failed.");
            }

            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
        => await DisposeAsync().AsTask().WaitAsync(cancellationToken);

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

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        try
        {
            var (from, to) = GetFilter(grainId, reminderName, eTag);
            long removed = await _db.SortedSetRemoveRangeByValueAsync(_hashSetKey, from, to);
            return removed > 0;
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task TestOnlyClearTable()
    {
        try
        {
            await _db.KeyDeleteAsync(_hashSetKey);
        }
        catch (Exception exception)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public async Task<string> UpsertRow(ReminderEntry entry)
    {
        const string UpsertScript =
            """
            local key = KEYS[1]
            local expectedFrom = '[' .. ARGV[1]
            local expectedTo = '[' .. ARGV[2]
            local allFrom = '[' .. ARGV[3]
            local allTo = '[' .. ARGV[4]
            local value = ARGV[5]
            local expectedETag = ARGV[6]
            local expiryMilliseconds = tonumber(ARGV[7])

            if expectedETag == '' then
                local existing = redis.call('ZRANGEBYLEX', key, allFrom, allTo, 'LIMIT', 0, 1)
                if #existing ~= 0 then
                    return 0
                end
            elseif redis.call('ZREMRANGEBYLEX', key, expectedFrom, expectedTo) ~= 1 then
                return 0
            end

            redis.call('ZADD', key, 0, value)
            if expiryMilliseconds > 0 then
                redis.call('PEXPIRE', key, expiryMilliseconds)
            else
                redis.call('PERSIST', key)
            end
            return 1
            """;

        try
        {
            LogDebugUpsertRow(new(entry), entry.ETag);

            var (newETag, value) = ConvertFromEntry(entry);
            var (expectedFrom, expectedTo) = GetFilter(entry.GrainId, entry.ReminderName, entry.ETag);
            var (allFrom, allTo) = GetFilter(entry.GrainId, entry.ReminderName);
            var expiryMilliseconds = _redisOptions.EntryExpiry is { } expiry
                ? Math.Max(1, (long)Math.Ceiling(expiry.TotalMilliseconds))
                : -1;
            var result = await _db.ScriptEvaluateAsync(
                UpsertScript,
                keys: new[] { _hashSetKey },
                values: new RedisValue[] { expectedFrom, expectedTo, allFrom, allTo, value, entry.ETag, expiryMilliseconds });
            if ((long)result != 1)
            {
                throw new Runtime.ReminderException(
                    $"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
            }

            return newETag.ToString();
        }
        catch (Exception exception) when (exception is not Runtime.ReminderException)
        {
            throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
        }
    }

    public void Dispose()
    {
        var muxer = _muxer;
        if (muxer is null)
        {
            return;
        }

        var muxerIsShared = _muxerIsShared;
        _muxer = null!;
        _db = null!;
        _muxerIsShared = false;

        if (!muxerIsShared)
        {
            muxer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var muxer = _muxer;
        if (muxer is null)
        {
            return;
        }

        var muxerIsShared = _muxerIsShared;
        _muxer = null!;
        _db = null!;
        _muxerIsShared = false;

        if (!muxerIsShared)
        {
            await muxer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task DisposeMultiplexerWhenCreatedAsync(
        Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> creationTask)
    {
        try
        {
            var (multiplexer, isShared) = await creationTask.ConfigureAwait(false);
            if (!isShared)
            {
                await multiplexer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Observe a late connection failure after initialization was canceled.
        }
    }

    internal static ReminderEntry ConvertToEntry(RedisValue reminderValue)
    {
        var byteCount = reminderValue.GetByteCount();
        var payload = ArrayPool<byte>.Shared.Rent(byteCount + 2);
        try
        {
            payload[0] = (byte)'[';
            reminderValue.CopyTo(payload.AsSpan(1, byteCount));
            payload[byteCount + 1] = (byte)']';
            var reader = new Utf8JsonReader(payload.AsSpan(0, byteCount + 2));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new FormatException("Reminder payload is not a JSON array.");
            }

            _ = ReadRequiredString(ref reader, 0); // Grain hash, used by the Redis lexicographical index.
            var entry = new ReminderEntry
            {
                GrainId = GrainId.Parse(ReadRequiredString(ref reader, 1)),
                ReminderName = ReadRequiredString(ref reader, 2),
                ETag = ReadRequiredString(ref reader, 3),
                StartAt = ReadRequiredDateTime(ref reader, 4),
                Period = ReadRequiredTimeSpan(ref reader, 5),
                CronExpression = ReadNullableString(ref reader),
                NextDueUtc = ReadNullableDateTime(ref reader),
                LastFireUtc = ReadNullableDateTime(ref reader),
                Priority = ReadPriority(ref reader),
                Action = ReadMissedReminderAction(ref reader),
                CronTimeZoneId = ReadNullableString(ref reader),
                ScheduleId = ReadNullableString(ref reader),
                JobId = ReadNullableString(ref reader),
                JobShardId = ReadNullableString(ref reader),
            };

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                reader.Skip();
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new FormatException("Reminder payload is not a complete JSON array.");
            }

            return entry;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private static string ReadRequiredString(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? throw new FormatException($"Reminder payload segment {index} is null."),
            JsonTokenType.Null => throw new FormatException($"Reminder payload segment {index} is null."),
            _ => throw new FormatException($"Reminder payload segment {index} must be a string."),
        };
    }

    private static string ReadNullableString(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return string.Empty;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return string.Empty;
        }

        var value = reader.GetString();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static DateTime ReadRequiredDateTime(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new FormatException($"Reminder payload segment {index} must be a timestamp string.");
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out DateTime result, out var bytesConsumed, 'O')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        return DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static TimeSpan ReadRequiredTimeSpan(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new FormatException($"Reminder payload segment {index} must be a duration string.");
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out TimeSpan result, out var bytesConsumed, 'c')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        return TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadNullableDateTime(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.IsEmpty)
        {
            return null;
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out DateTime result, out var bytesConsumed, 'O')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        var value = reader.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static DurableJobPriority ReadPriority(ref Utf8JsonReader reader)
    {
        if (!TryReadInt32(ref reader, out var value))
        {
            return DurableJobPriority.Normal;
        }

        return ParsePriority(value);
    }

    private static MissedReminderAction ReadMissedReminderAction(ref Utf8JsonReader reader)
    {
        if (!TryReadInt32(ref reader, out var value))
        {
            return MissedReminderAction.Skip;
        }

        return ParseAction(value);
    }

    private static bool TryReadInt32(ref Utf8JsonReader reader, out int value)
    {
        value = default;
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out value);
        }

        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static DurableJobPriority ParsePriority(int value) => value switch
    {
        (int)DurableJobPriority.Low => DurableJobPriority.Low,
        (int)DurableJobPriority.High => DurableJobPriority.High,
        (int)DurableJobPriority.Normal => DurableJobPriority.Normal,
        _ => DurableJobPriority.Normal,
    };

    private static MissedReminderAction ParseAction(int value) => value switch
    {
        (int)MissedReminderAction.FireImmediately => MissedReminderAction.FireImmediately,
        (int)MissedReminderAction.Skip => MissedReminderAction.Skip,
        (int)MissedReminderAction.Notify => MissedReminderAction.Notify,
        _ => MissedReminderAction.Skip,
    };

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

    private (RedisValue eTag, RedisValue value) ConvertFromEntry(ReminderEntry entry)
    {
        string grainHash = entry.GrainId.GetUniformHashCode().ToString("X8");
        string eTag = Guid.NewGuid().ToString("N");
        return (eTag, SerializeEntry(entry, grainHash, eTag));
    }

    private static string SerializeSegments(IReadOnlyList<string> segments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < segments.Count; i++)
            {
                writer.WriteStringValue(segments[i]);
            }

            writer.WriteEndArray();
        }

        return TrimArrayDelimiters(buffer.WrittenSpan);
    }

    private static string SerializeEntry(ReminderEntry entry, string grainHash, string eTag)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(grainHash);
            writer.WriteStringValue(entry.GrainId.ToString());
            writer.WriteStringValue(entry.ReminderName);
            writer.WriteStringValue(eTag);
            WriteDateTimeValue(writer, entry.StartAt);
            WriteTimeSpanValue(writer, entry.Period);
            writer.WriteStringValue(entry.CronExpression ?? string.Empty);
            WriteNullableDateTimeValue(writer, entry.NextDueUtc);
            WriteNullableDateTimeValue(writer, entry.LastFireUtc);
            writer.WriteNumberValue((int)entry.Priority);
            writer.WriteNumberValue((int)entry.Action);
            writer.WriteStringValue(entry.CronTimeZoneId ?? string.Empty);
            writer.WriteStringValue(entry.ScheduleId ?? string.Empty);
            writer.WriteStringValue(entry.JobId ?? string.Empty);
            writer.WriteStringValue(entry.JobShardId ?? string.Empty);
            writer.WriteEndArray();
        }

        return TrimArrayDelimiters(buffer.WrittenSpan);
    }

    private static void WriteDateTimeValue(Utf8JsonWriter writer, DateTime value)
    {
        Span<char> formatted = stackalloc char[33];
        if (!value.TryFormat(formatted, out var charsWritten, "O", CultureInfo.InvariantCulture))
        {
            throw new FormatException("Could not format reminder timestamp.");
        }

        writer.WriteStringValue(formatted[..charsWritten]);
    }

    private static void WriteNullableDateTimeValue(Utf8JsonWriter writer, DateTime? value)
    {
        if (value is { } timestamp)
        {
            WriteDateTimeValue(writer, timestamp);
        }
        else
        {
            writer.WriteStringValue(string.Empty);
        }
    }

    private static void WriteTimeSpanValue(Utf8JsonWriter writer, TimeSpan value)
    {
        Span<char> formatted = stackalloc char[26];
        if (!value.TryFormat(formatted, out var charsWritten, "c", CultureInfo.InvariantCulture))
        {
            throw new FormatException("Could not format reminder period.");
        }

        writer.WriteStringValue(formatted[..charsWritten]);
    }

    private static string TrimArrayDelimiters(ReadOnlySpan<byte> json)
    {
        if (json.Length < 2 || json[0] != (byte)'[' || json[^1] != (byte)']')
        {
            throw new FormatException("Reminder payload is not a JSON array.");
        }

        return Encoding.UTF8.GetString(json[1..^1]);
    }

    private readonly struct ReminderEntryLogValue(ReminderEntry entry)
    {
        public override string ToString() => entry.ToString() ?? string.Empty;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpsertRow entry = {Entry}, ETag = {ETag}"
    )]
    private partial void LogDebugUpsertRow(ReminderEntryLogValue entry, string eTag);
}
