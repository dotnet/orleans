using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using Orleans.Configuration;
using Orleans.Runtime;

using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.Reminders.Redis
{
    internal class RedisReminderTable : IReminderTable
    {
        private readonly RedisKey _hashSetKey;
        private readonly RedisReminderTableOptions _redisOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ILogger _logger;
        private IConnectionMultiplexer _muxer;
        private IDatabase _db;

        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings()
        {
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public RedisReminderTable(
            ILogger<RedisReminderTable> logger,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<RedisReminderTableOptions> redisOptions)
        {
            _redisOptions = redisOptions.Value;
            _clusterOptions = clusterOptions.Value;
            _logger = logger;

            _hashSetKey = Encoding.UTF8.GetBytes($"{_clusterOptions.ServiceId}/reminders");
        }

        public async Task Init()
        {
            try
            {
                _muxer = await _redisOptions.CreateMultiplexer(_redisOptions);
                _db = _muxer.GetDatabase();

                if (_redisOptions.EntryExpiry is { } expiry)
                {
                    await _db.KeyExpireAsync(_hashSetKey, expiry);
                }
            }
            catch (Exception exception)
            {
                throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
            }
        }

        public async Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
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
                    return ConvertToEntry(values.SingleOrDefault());
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
                local from = '[' .. ARGV[1] -- start of the conditional (with etag) key range
                local to = '[' .. ARGV[2] -- end of the conditional (with etag) key range
                local value = ARGV[3]

                -- Remove all entries for this reminder
                local remRes = redis.call('ZREMRANGEBYLEX', key, from, to);

                -- Add the new reminder entry
                local addRes = redis.call('ZADD', key, 0, value);
                return { key, from, to, value, remRes, addRes }
                """;

            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("UpsertRow entry = {Entry}, ETag = {ETag}", entry.ToString(), entry.ETag);
                }

                var (newETag, value) = ConvertFromEntry(entry);
                var (from, to) = GetFilter(entry.GrainId, entry.ReminderName);
                var res = await _db.ScriptEvaluateAsync(UpsertScript, keys: new[] { _hashSetKey }, values: new[] { from, to, value });
                return newETag;
            }
            catch (Exception exception) when (exception is not ReminderException)
            {
                throw new RedisRemindersException(Invariant($"{exception.GetType()}: {exception.Message}"));
            }
        }

        private static ReminderEntry ConvertToEntry(string reminderValue)
        {
            string[] segments = JsonConvert.DeserializeObject<string[]>($"[{reminderValue}]");

            return new ReminderEntry
            {
                GrainId = GrainId.Parse(segments[1]),
                ReminderName = segments[2],
                ETag = segments[3],
                StartAt = DateTime.Parse(segments[4], null, DateTimeStyles.RoundtripKind),
                Period = TimeSpan.Parse(segments[5]),
            };
        }

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
            string prefix = JsonConvert.SerializeObject(segments, _jsonSettings);
            return ($"{prefix[1..^1]},\"", $"{prefix[1..^1]},#");
        }

        private (RedisValue eTag, RedisValue value) ConvertFromEntry(ReminderEntry entry)
        {
            string grainHash = entry.GrainId.GetUniformHashCode().ToString("X8");
            string eTag = Guid.NewGuid().ToString();
            string[] segments = new string[]
            {
                grainHash,
                entry.GrainId.ToString(),
                entry.ReminderName,
                eTag,
                entry.StartAt.ToString("O"),
                entry.Period.ToString()
            };

            return (eTag, JsonConvert.SerializeObject(segments, _jsonSettings)[1..^1]);
        }
    }
}
