using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Storage;

/// <summary>
/// Wrapper/Helper class around StackExchange redis stream
/// </summary>    
internal partial class RedisStreamStorage
{
    public const int MaxNumberOfMsgToGet = 1000;

    private readonly string _streamQueueIdName;
    private readonly RedisStreamOptions _redisStreamOptions;
    private readonly RedisStreamReceiverOptions _redisStreamReceiverOptions;
    private readonly ILogger<RedisStreamStorage> _logger;

    private readonly RedisKey _streamRedisKey;
    private IDatabase? _database;    

    public RedisStreamStorage(
        QueueId queueId,
        ClusterOptions clusterOptions,
        RedisStreamOptions redisStreamOptions,
        RedisStreamReceiverOptions redisStreamReceiverOptions,
        ILoggerFactory loggerFactory)
    {
        _streamQueueIdName = queueId.ToString();
        _redisStreamOptions = redisStreamOptions;
        _redisStreamReceiverOptions = redisStreamReceiverOptions;
        _logger = loggerFactory.CreateLogger<RedisStreamStorage>();

        _streamRedisKey = _redisStreamOptions.GetRedisKey(clusterOptions, queueId);
    }

    private IDatabase Database => _database ?? throw new InvalidOperationException("Stream not initialized");

    public async Task InitializeAsync()
    {
        await ConnectAsync();
        await CreateGroupAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            _database ??= (await _redisStreamOptions.CreateMultiplexer.Invoke(_redisStreamOptions)).GetDatabase();
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(ConnectAsync));
        }
    }

    private async Task CreateGroupAsync()
    {
        try
        {
            await Database
                .StreamCreateConsumerGroupAsync(_streamRedisKey, _redisStreamReceiverOptions.ConsumerGroupName, position: 0, createStream: true);
        }
        catch (RedisServerException exc) when (exc.Message.Equals("BUSYGROUP Consumer Group name already exists"))
        {
            // The group already exists, so we can ignore this exception.
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(CreateGroupAsync));
        }
    }

    public async Task<StreamEntry> AddEntryAsync(StreamEntry entry)
    {
        var id = RedisValue.Null;

        try
        {
            id = await Database.StreamAddAsync(_streamRedisKey, entry.Values);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(AddEntryAsync));
        }

        return new StreamEntry(id, entry.Values);
    }

    public async Task<IEnumerable<StreamEntry>> GetEntriesAsync(int count)
    {
        IEnumerable<StreamEntry> entriesResult = [];
        try
        {
            var claimResult = await Database.StreamAutoClaimAsync(_streamRedisKey,
                _redisStreamReceiverOptions.ConsumerGroupName,
                _redisStreamReceiverOptions.ConsumerName,
                (long)_redisStreamReceiverOptions.DeliveredMessageIdleTimeout.TotalMilliseconds,
                startAtId: 0, count);

            if (claimResult.ClaimedEntries.Length == count)
            {
                entriesResult = claimResult.ClaimedEntries;
            }
            else
            {
                var entriesReadGroup = await Database.StreamReadGroupAsync(_streamRedisKey,
                    _redisStreamReceiverOptions.ConsumerGroupName,
                    _redisStreamReceiverOptions.ConsumerName, position: ">",
                    count - claimResult.ClaimedEntries.Length);

                entriesResult = claimResult.ClaimedEntries.Length != 0 ?
                    claimResult.ClaimedEntries.Concat(entriesReadGroup) : entriesReadGroup;
            }

        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(GetEntriesAsync));
        }
        return entriesResult;
    }

    public async Task EntriesAcknowledgeAsync(IEnumerable<StreamEntry> streamEntries)
    {
        const string DeliveredScript =
            """
            local ack = redis.call('XACK', KEYS[1], ARGV[1], unpack(ARGV, 2))
            local delete = redis.call('XDEL', KEYS[1], unpack(ARGV, 2))
            return { ack, delete }
            """;

        var streamEntriesId = streamEntries.Select(x => x.Id).ToArray();
        var args = new RedisValue[streamEntriesId.Length + 1];
        args[0] = _redisStreamReceiverOptions.ConsumerGroupName;
        Array.Copy(streamEntriesId, 0, args, 1, streamEntriesId.Length);
        try
        {
            await Database.ScriptEvaluateAsync(DeliveredScript, keys: [_streamRedisKey], values: args);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(EntriesAcknowledgeAsync));
        }
    }

    [DoesNotReturn]
    private void ReportErrorAndRethrow(Exception exc, string operation)
    {
        LogErrorRedisOperation(exc, operation, _streamQueueIdName);
        throw new AggregateException($"Error doing {operation} for Redis stream {_streamQueueIdName}", exc);
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.StreamProviderManagerBase,
        Level = LogLevel.Error,
        Message = "Error doing {Operation} for Redis stream {StreamName}"
    )]
    private partial void LogErrorRedisOperation(Exception exception, string operation, string streamName);
}
