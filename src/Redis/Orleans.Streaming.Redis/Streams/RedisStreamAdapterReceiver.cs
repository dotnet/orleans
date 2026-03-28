using Microsoft.Extensions.Logging;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

internal partial class RedisStreamAdapterReceiver : IQueueAdapterReceiver
{
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter;
    private RedisStreamStorage streamStorage;
    private readonly QueueId queueId;
    private readonly ILogger<RedisStreamAdapterReceiver> logger;

    private readonly List<RedisStreamPendingMessage> pendingMessages = [];

    private Task outstandingTask;
    private long lastSequenceId;

    internal static IQueueAdapterReceiver Create(QueueId queueId,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        RedisStreamStorage storage, ILoggerFactory loggerFactory)
    {
        if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return new RedisStreamAdapterReceiver(queueId, dataAdapter, storage, loggerFactory.CreateLogger<RedisStreamAdapterReceiver>());
    }

    private RedisStreamAdapterReceiver(
        QueueId queueId,
        IQueueDataAdapter<StreamEntry, IBatchContainer> dataAdapter,
        RedisStreamStorage streamStorage,
        ILogger<RedisStreamAdapterReceiver> logger)
    {
        this.dataAdapter = dataAdapter;
        this.streamStorage = streamStorage;
        this.queueId = queueId;
        this.logger = logger;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        if (streamStorage != null) // check in case we already shut it down.
        {
            await streamStorage.InitializeAsync();
        }
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

            var count = maxCount is < 0 or QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG
                ? RedisStreamStorage.MaxNumberOfMsgToGet
                : Math.Min(maxCount, RedisStreamStorage.MaxNumberOfMsgToGet);

            var task = streamStorageRef
                .GetEntriesAsync(count);

            outstandingTask = task;

            var streamEntries = await task;

            var messagesBatch = new List<IBatchContainer>();
            foreach (var streamEntry in streamEntries)
            {
                var container = dataAdapter.FromQueueMessage(streamEntry, lastSequenceId++);
                messagesBatch.Add(container);

                pendingMessages.Add(new RedisStreamPendingMessage(streamEntry, container.SequenceToken));
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

            // get the stream entries for all messages deliveries that were delivered.
            var pendingMessagesStreamEntries = pendingMessagesToRemove
                .Where(pendingMessage => deliveredTokens.Contains(pendingMessage.Token))
                .Select(pendingMessage => pendingMessage.StreamEntry)
                .ToList();

            if (pendingMessagesStreamEntries.Count == 0)
                return;

            // Acknowledge all delivered messages.
            outstandingTask = streamStorageRef.EntriesAcknowledgeAsync(pendingMessagesStreamEntries);
            try
            {
                await outstandingTask;
            }
            catch (Exception exc)
            {
                LogWarningOperationException(logger, exc, nameof(streamStorageRef.EntriesAcknowledgeAsync), queueId);
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
}
