using System;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Streams;
using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.Streaming.Redis;

internal sealed class RedisStreamStorage
{
    public const int MaxNumberOfMsgToGet = 1000;

    private readonly RedisStreamingOptions _redisOptions;
    private readonly RedisStreamReceiverOptions _receiverOptions;
    private readonly RedisKey _key;
    private readonly RedisKey _checkpointKey;
    private IDatabase? _db;
    private RedisStreamCheckpointer? _checkpointer;
    private string? _readOffset;

    public RedisStreamStorage(
        string providerName,
        ClusterOptions clusterOptions,
        RedisStreamingOptions redisOptions,
        RedisStreamReceiverOptions receiverOptions,
        QueueId queueId)
    {
        _redisOptions = redisOptions;
        _receiverOptions = receiverOptions;
        _key = $"{clusterOptions.ServiceId}/streaming/{providerName}/{queueId}";
        _checkpointKey = $"{_key}/checkpoint";
        QueueId = queueId;
    }

    public QueueId QueueId { get; }

    public string FieldName => _receiverOptions.FieldName;

    public async Task ConnectAsync()
    {
        try
        {
            if (_db is null)
            {
                _db = (await _redisOptions.CreateMultiplexer.Invoke(_redisOptions)).GetDatabase();
                _checkpointer = new RedisStreamCheckpointer(
                    _db,
                    _checkpointKey,
                    _redisOptions.CheckpointPersistInterval,
                    _redisOptions.EntryExpiry);
                await _checkpointer.LoadAsync();
                _readOffset = _checkpointer.Offset;
            }
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"), ex);
        }
    }

    public async Task AddMessageAsync(RedisValue payload)
    {
        try
        {
            var db = _db ?? throw new InvalidOperationException("Redis stream storage is not connected.");

            await db.StreamAddAsync(
                _key,
                _receiverOptions.FieldName,
                payload,
                maxLength: _redisOptions.MaxStreamLength,
                useApproximateMaxLength: _redisOptions.UseApproximateMaxLength);

            if (_redisOptions.EntryExpiry is { } expiry)
            {
                await db.KeyExpireAsync(_key, expiry);
                await db.KeyExpireAsync(_checkpointKey, expiry);
            }
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"), ex);
        }
    }

    public async Task<StreamEntry[]> GetMessagesAsync(int count)
    {
        try
        {
            _ = _checkpointer ?? throw new InvalidOperationException("Redis stream storage is not connected.");
            var readCount = Math.Min(count, _receiverOptions.ReadCount);
            if (readCount <= 0)
            {
                return [];
            }

            return await ReadFromOffsetAsync(readCount);
        }
        catch (Exception ex)
        {
            throw new RedisStreamingException(Invariant($"{ex.GetType()}: {ex.Message}"), ex);
        }
    }

    public Task DeliveredMessagesAsync(StreamEntry entry)
    {
        var checkpointer = _checkpointer ?? throw new InvalidOperationException("Redis stream storage is not connected.");
        checkpointer.Update(entry.Id.ToString(), DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (_checkpointer is { } checkpointer)
        {
            await checkpointer.FlushAsync();
        }
    }

    private async Task<StreamEntry[]> ReadFromOffsetAsync(int readCount)
    {
        var db = _db ?? throw new InvalidOperationException("Redis stream storage is not connected.");
        var startEntryId = string.IsNullOrEmpty(_readOffset)
            ? "-"
            : GetNextEntryId(_readOffset);

        var entries = await db.StreamRangeAsync(_key, startEntryId, "+", readCount, Order.Ascending);

        if (entries.Length > 0)
        {
            _readOffset = entries[^1].Id.ToString();
        }

        return entries;
    }

    internal static string GetNextEntryId(string entryId)
    {
        var (sequenceNumber, redisSequenceNumber) = RedisStreamBatchContainer.ParseEntryId(entryId);

        if (redisSequenceNumber == long.MaxValue)
        {
            if (sequenceNumber == long.MaxValue)
            {
                throw new OverflowException(Invariant($"Redis stream entry identifier '{entryId}' cannot be advanced."));
            }

            sequenceNumber++;
            redisSequenceNumber = 0;
        }
        else
        {
            redisSequenceNumber++;
        }

        return Invariant($"{sequenceNumber}-{redisSequenceNumber}");
    }
}
