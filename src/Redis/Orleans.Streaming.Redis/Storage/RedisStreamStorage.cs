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
    private IConnectionMultiplexer? _multiplexer;
    private bool _isSharedMultiplexer;
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
                var (multiplexer, isSharedMultiplexer) = await _redisOptions.CreateMultiplexer.Invoke(_redisOptions);

                try
                {
                    var database = multiplexer.GetDatabase();
                    var checkpointer = new RedisStreamCheckpointer(
                        database,
                        _checkpointKey,
                        _redisOptions.CheckpointPersistInterval,
                        _redisOptions.EntryExpiry);
                    await checkpointer.LoadAsync();

                    _multiplexer = multiplexer;
                    _isSharedMultiplexer = isSharedMultiplexer;
                    _db = database;
                    _checkpointer = checkpointer;
                    _readOffset = checkpointer.Offset;
                }
                catch
                {
                    if (!isSharedMultiplexer)
                    {
                        await DisposeMultiplexerAsync(multiplexer);
                    }

                    throw;
                }
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
        var checkpointer = _checkpointer;
        var multiplexer = _multiplexer;
        var isSharedMultiplexer = _isSharedMultiplexer;

        _checkpointer = null;
        _readOffset = null;
        _db = null;
        _multiplexer = null;
        _isSharedMultiplexer = false;

        if (checkpointer is not null)
        {
            await checkpointer.FlushAsync();
        }

        if (multiplexer is not null && !isSharedMultiplexer)
        {
            await DisposeMultiplexerAsync(multiplexer);
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

    private static async Task DisposeMultiplexerAsync(IConnectionMultiplexer multiplexer)
    {
        try
        {
            await multiplexer.CloseAsync();
        }
        finally
        {
            multiplexer.Dispose();
        }
    }
}
