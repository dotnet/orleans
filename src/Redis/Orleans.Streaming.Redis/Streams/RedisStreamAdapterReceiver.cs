using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

internal partial class RedisStreamAdapterReceiver : IQueueAdapterReceiver
{
    private readonly RedisStreamOptions options;
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter;
    private RedisStreamStorage streamStorage;
    private readonly QueueId queueId;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RedisStreamAdapterReceiver> logger;

    private readonly List<PendingMessageAcknowledge> pendingMessages = [];

    private Task outstandingTask;
    private string lastId = "$";
    private long lastSequenceId;

    private DateTimeOffset lastTrimTime;

    internal static IQueueAdapterReceiver Create(RedisStreamOptions options,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter, RedisStreamStorage storage,
        QueueId queueId, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return new RedisStreamAdapterReceiver(options, dataAdapter, storage, queueId, timeProvider, loggerFactory.CreateLogger<RedisStreamAdapterReceiver>());
    }

    private RedisStreamAdapterReceiver(
        RedisStreamOptions options,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        RedisStreamStorage streamStorage,
        QueueId queueId, TimeProvider timeProvider,
        ILogger<RedisStreamAdapterReceiver> logger)
    {
        this.options = options;
        this.dataAdapter = dataAdapter;
        this.streamStorage = streamStorage;
        this.queueId = queueId;
        this.timeProvider = timeProvider;
        this.logger = logger;

        lastTrimTime = timeProvider.GetUtcNow();
    }

    public Task Initialize(TimeSpan timeout)
    {
        if (streamStorage != null) // check in case we already shut it down.
        {
            return streamStorage.InitAsync();
        }
        return Task.CompletedTask;
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            // await the last storage operation, so after we shutdown and stop this receiver we don't get async operation completions from pending storage operations.
            if (outstandingTask != null)
                await outstandingTask;
        }
        finally
        {
            // remember that we shut down so we never try to read from the queue again.
            streamStorage = null;
        }
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        try
        {
            var streamStorageRef = streamStorage; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
            if (streamStorageRef == null)
                return [];

            var task = streamStorageRef
                .GetEntriesAsync(lastId, maxCount);

            outstandingTask = task;
            lastId = ">";

            var streamEntries = await task;

            var messagesBatch = new List<IBatchContainer>();
            foreach (var streamEntry in streamEntries)
            {
                var container = dataAdapter.FromQueueMessage(streamEntry, lastSequenceId++);
                messagesBatch.Add(container);

                pendingMessages.Add(new PendingMessageAcknowledge(streamEntry, container.SequenceToken));                
            }           

            return messagesBatch;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading from stream {QueueId}", queueId);
            return default;
        }
        finally
        {
            outstandingTask = null;

            await TrimStorageAsyncIfNeeded();
        }
    }

    private async Task TrimStorageAsyncIfNeeded()
    {
        try
        {
            if (timeProvider.GetUtcNow() - lastTrimTime < TimeSpan.FromMinutes(options.TrimTimeMinutes))
                return;

            var streamStorageRef = streamStorage; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
            if (streamStorageRef == null)
                return;

            outstandingTask = streamStorageRef.TrimAsync(options.MaxStreamLength, true);
            try
            {
                await outstandingTask;
                lastTrimTime = timeProvider.GetUtcNow();
            }
            catch (Exception exc)
            {
                LogWarningOperationException(logger, exc, nameof(streamStorageRef.EntryAcknowledgeAsync), queueId);
            }
        }
        finally
        {
            outstandingTask = null;
        }
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        try
        {
            var streamStorageRef = streamStorage; // store direct ref, in case we are somehow asked to shutdown while we are receiving.            
            if (messages.Count == 0 || streamStorageRef == null)
                return;

            // get sequence tokens of delivered messages
            var deliveredTokens = messages.Select(m => m.SequenceToken).ToList();

            // find most recent (newest) delivered message token
            StreamSequenceToken newestToken = deliveredTokens.Max();

            // select all pending messages at or befor the newest delivered token
            var pendingMessagesToRemove = pendingMessages
                .Where(pendingMessage => !pendingMessage.Token.Newer(newestToken))
                .ToList();

            if (pendingMessagesToRemove.Count == 0) 
                return;

            // remove all pending messages at or befor the oldest token from pending, regardless of if it was acknowledge or not.
            foreach (var pendingMessage in pendingMessagesToRemove)
                pendingMessages.Remove(pendingMessage);

            // get the stream entries id for all messages deliveries that were delivered.
            var pendingMessagesStreamEntriesId = pendingMessagesToRemove
                .Where(pendingMessage => deliveredTokens.Contains(pendingMessage.Token))
                .Select(pendingMessage => pendingMessage.StreamEntry.Id)
                .ToList();

            if (pendingMessagesStreamEntriesId.Count == 0) 
                return;            

            // Acknowledge all delivered messages.
            outstandingTask = Task.WhenAll(pendingMessagesStreamEntriesId.Select(streamStorageRef.EntryAcknowledgeAsync));
            try
            {
                await outstandingTask;
            }
            catch (Exception exc)
            {
                LogWarningOperationException(logger, exc, nameof(streamStorageRef.EntryAcknowledgeAsync), queueId);
            }
        }
        finally
        {
            outstandingTask = null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception upon {Operation} on queue {QueueId}. Ignoring."
    )]
    private static partial void LogWarningOperationException(ILogger logger, Exception exception, string operation, QueueId queueId);

    private record PendingMessageAcknowledge
    {
        public PendingMessageAcknowledge(StreamEntry streamEntry, StreamSequenceToken token)
        {
            Token = token;
            StreamEntry = streamEntry;
        }

        public StreamEntry StreamEntry { get; }

        public StreamSequenceToken Token { get; }
    }
}
