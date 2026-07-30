using System;
using System.Buffers;
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
using Orleans.Runtime;

using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.AdvancedReminders.Redis
{
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

        public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            try
            {
                var (from, to) = GetFilter(grainId, reminderName);
                RedisValue[] values = await _db.SortedSetRangeByValueAsync(_hashSetKey, from, to);
                if (values.Length == 0)
                {
                    return null!;
                }
                else
                {
                    return ConvertToEntry(values.Single().ToString());
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
                IEnumerable<ReminderEntry> records = values.Select(static v => ConvertToEntry(v.ToString()));
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

                IEnumerable<ReminderEntry> records = values.Select(static v => ConvertToEntry(v.ToString()));
                return new ReminderTableData(records);
            }
            catch (Exception exception)
            {
                throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
            }
        }

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

        private static ReminderEntry ConvertToEntry(string reminderValue)
        {
            using var document = JsonDocument.Parse(CreatePayloadBuffer(reminderValue));
            var segments = document.RootElement;

            return new ReminderEntry
            {
                GrainId = GrainId.Parse(ReadRequiredString(segments, 1)),
                ReminderName = ReadRequiredString(segments, 2),
                ETag = ReadRequiredString(segments, 3),
                StartAt = DateTime.Parse(ReadRequiredString(segments, 4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Period = TimeSpan.Parse(ReadRequiredString(segments, 5), CultureInfo.InvariantCulture),
                CronExpression = ReadNullableString(segments, 6),
                NextDueUtc = ReadNullableDateTime(segments, 7),
                LastFireUtc = ReadNullableDateTime(segments, 8),
                Priority = ReadReminderPriority(segments, 9),
                Action = ReadMissedReminderAction(segments, 10),
                CronTimeZoneId = ReadNullableString(segments, 11),
                ScheduleId = ReadNullableString(segments, 12),
                JobId = ReadNullableString(segments, 13),
                JobShardId = ReadNullableString(segments, 14),
            };
        }

        private static byte[] CreatePayloadBuffer(string reminderValue)
            => Encoding.UTF8.GetBytes(string.Concat("[", reminderValue, "]"));

        private static string ReadRequiredString(JsonElement segments, int index)
        {
            if (segments.GetArrayLength() <= index)
            {
                throw new FormatException($"Reminder payload is missing segment {index}.");
            }

            var segment = segments[index];
            return segment.ValueKind switch
            {
                JsonValueKind.String => segment.GetString() ?? throw new FormatException($"Reminder payload segment {index} is null."),
                JsonValueKind.Null => throw new FormatException($"Reminder payload segment {index} is null."),
                _ => segment.ToString(),
            };
        }

        private static string ReadNullableString(JsonElement segments, int index)
        {
            if (segments.GetArrayLength() <= index)
            {
                return string.Empty;
            }

            var segment = segments[index];
            if (segment.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return string.Empty;
            }

            var value = segment.ValueKind is JsonValueKind.String
                ? segment.GetString()
                : segment.ToString();
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }

        private static DateTime? ReadNullableDateTime(JsonElement segments, int index)
        {
            var value = ReadNullableString(segments, index);
            return string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        private static ReminderPriority ReadReminderPriority(JsonElement segments, int index)
        {
            if (!TryReadInt32(segments, index, out var value))
            {
                return ReminderPriority.Normal;
            }

            return ParsePriority(value);
        }

        private static MissedReminderAction ReadMissedReminderAction(JsonElement segments, int index)
        {
            if (!TryReadInt32(segments, index, out var value))
            {
                return MissedReminderAction.Skip;
            }

            return ParseAction(value);
        }

        private static bool TryReadInt32(JsonElement segments, int index, out int value)
        {
            value = default;
            if (segments.GetArrayLength() <= index)
            {
                return false;
            }

            var token = segments[index];
            if (token.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            if (token.ValueKind is JsonValueKind.Number)
            {
                return token.TryGetInt32(out value);
            }

            var text = token.ValueKind is JsonValueKind.String
                ? token.GetString()
                : token.ToString();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static ReminderPriority ParsePriority(int value) => value switch
        {
            (int)ReminderPriority.High => ReminderPriority.High,
            (int)ReminderPriority.Normal => ReminderPriority.Normal,
            _ => ReminderPriority.Normal,
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
            string eTag = Guid.NewGuid().ToString();
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
                writer.WriteStringValue(entry.StartAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteStringValue(entry.Period.ToString("c", CultureInfo.InvariantCulture));
                writer.WriteStringValue(entry.CronExpression ?? string.Empty);
                writer.WriteStringValue(entry.NextDueUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                writer.WriteStringValue(entry.LastFireUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
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
}
