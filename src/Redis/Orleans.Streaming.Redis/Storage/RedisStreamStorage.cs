using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Storage;

/// <summary>
/// Wrapper/Helper class around StackExchange redis stream
/// </summary>    
internal partial class RedisStreamStorage(IConnectionMultiplexer connectionMultiplexer,
    RedisKey streamKey, string streamName, ILoggerFactory loggerFactory)
{
    private const string GROUP_NAME = "orleans-redis-stream-consumer";

    private readonly ILogger<RedisStreamStorage> logger = loggerFactory.CreateLogger<RedisStreamStorage>();

    private readonly IDatabase database = connectionMultiplexer.GetDatabase();

    public async Task InitAsync()
    {
        try
        {
            await database
                .StreamCreateConsumerGroupAsync(streamKey, GROUP_NAME, "$", true);
        }
        catch (Exception exc) when (exc.Message.Contains("name already exists"))
        {
            logger.LogInformation("Consumer group {Consumer} already exists for stream {StreamName}", GROUP_NAME, streamName);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(InitAsync));
        }
    }

    public async Task<StreamEntry> AddEntryAsync(StreamEntry entry)
    {
        var id = RedisValue.Null;

        try
        {
            id = await database.StreamAddAsync(streamKey, entry.Values);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(AddEntryAsync));
        }

        return new StreamEntry(id, entry.Values);
    }

    public async Task<IEnumerable<StreamEntry>> GetEntriesAsync(RedisValue? position = null, int? count = null)
    {
        IEnumerable<StreamEntry> entries = [];
        try
        {
            entries = await database.StreamReadGroupAsync(streamKey, GROUP_NAME, streamName, position ?? ">", count);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(GetEntriesAsync));
        }
        return entries;
    }

    public async Task EntryAcknowledgeAsync(RedisValue messageId)
    {
        try
        {
            await database.StreamAcknowledgeAsync(streamKey, GROUP_NAME, messageId);
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(EntryAcknowledgeAsync));
        }
    }

    public async Task TrimAsync(int maxLength, bool useApproximateMaxLength)
    {
        try
        {
            var trimMessagesCount = await database.StreamTrimAsync(streamKey, maxLength, useApproximateMaxLength);
            if (trimMessagesCount > 0)
            {
                logger.LogInformation("Trimmed Redis stream {StreamName} to max length {MaxLength}, removed {TrimmedCount} entries", streamName, maxLength, trimMessagesCount);
            }
        }
        catch (Exception exc)
        {
            ReportErrorAndRethrow(exc, nameof(TrimAsync));
        }
    }

    [DoesNotReturn]
    private void ReportErrorAndRethrow(Exception exc, string operation)
    {
        LogErrorRedisOperation(exc, operation, streamName);
        throw new AggregateException($"Error doing {operation} for Redis stream {streamName}", exc);
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.StreamProviderManagerBase,
        Level = LogLevel.Error,
        Message = "Error doing {Operation} for Redis stream {StreamName}"
    )]
    private partial void LogErrorRedisOperation(Exception exception, string operation, string streamName);
}
