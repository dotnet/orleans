using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamStorage
{
    public const int MaxNumberOfMsgToGet = 1000;

    private readonly ILogger _logger;
    private readonly RedisStreamingOptions _redisOptions;
    private readonly RedisStreamReceiverOptions _receiverOptions;
    private readonly RedisKey _key;
    private IDatabase _db;

    public RedisStreamStorage(
        ILoggerFactory loggerFactory,
        ClusterOptions clusterOptions,
        RedisStreamingOptions redisOptions,
        RedisStreamReceiverOptions receiverOptions,
        QueueId queueId)
    {
        _logger = loggerFactory.CreateLogger<RedisStreamStorage>();
        _redisOptions = redisOptions;
        _receiverOptions = receiverOptions;
        _key = $"{clusterOptions.ServiceId}/streaming/{queueId}";
        QueueId = queueId;
    }

    public QueueId QueueId { get; }

    public async Task ConnectAsync()
    {
        try
        {
            if (_db is null)
            {
                _db = (await _redisOptions.CreateMultiplexer.Invoke(_redisOptions)).GetDatabase();
                if (_redisOptions.EntryExpiry is { } expiry)
                {
                    await _db.KeyExpireAsync(_key, expiry);
                }
            }
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"));
        }
    }

    public async Task CreateGroupAsync()
    {
        try
        {
            try
            {
                await _db.StreamCreateConsumerGroupAsync(_key, _receiverOptions.ConsumerGroupName, position: 0, createStream: true);
            }
            catch (RedisServerException exception) when (exception.Message == "BUSYGROUP Consumer Group name already exists")
            {
            }
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"));
        }
    }

    public async Task AddMessageAsync(RedisValue payload)
    {
        try
        {
            await _db.StreamAddAsync(_key, _receiverOptions.FieldName, payload);
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"));
        }
    }

    public async Task<IEnumerable<StreamEntry>> GetMessagesAsync(int count)
    {
        try
        {
            var claimResult = await _db.StreamAutoClaimAsync(_key, _receiverOptions.ConsumerGroupName, _receiverOptions.ConsumerName, (long)_receiverOptions.DeliveredMessageIdleTimeout.TotalMilliseconds, startAtId: 0, count);
            if (claimResult.ClaimedEntries.Length == count)
            {
                return claimResult.ClaimedEntries;
            }
            var messages = await _db.StreamReadGroupAsync(_key, _receiverOptions.ConsumerGroupName, _receiverOptions.ConsumerName, position: ">", count - claimResult.ClaimedEntries.Length);
            return claimResult.ClaimedEntries.Length != 0 ? claimResult.ClaimedEntries.Concat(messages) : messages;
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"));
        }
    }

    public async Task DeliveredMessagesAsync(IEnumerable<StreamEntry> messages)
    {
        const string DeliveredScript =
            """
            local ack = redis.call('XACK', KEYS[1], ARGV[1], table.unpack(ARGV, 2))
            local delete = redis.call('XDEL', KEYS[1], table.unpack(ARGV, 2))
            return { ack, delete }
            """;

        var messageIds = messages.Select(x => x.Id).ToArray();
        var args = new RedisValue[messageIds.Length + 1];
        args[0] = _receiverOptions.ConsumerGroupName;
        Array.Copy(messageIds, 0, args, 1, messageIds.Length);
        try
        {
            await _db.ScriptEvaluateAsync(DeliveredScript, keys: new[] { _key }, values: args);
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"));
        }
    }
}
