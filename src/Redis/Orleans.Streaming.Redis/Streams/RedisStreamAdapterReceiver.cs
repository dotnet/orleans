using Microsoft.Extensions.Logging;
using Orleans.Streaming.Redis.Storage;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

internal partial class RedisStreamAdapterReceiver : IQueueAdapterReceiver
{
    private readonly IQueueDataAdapter<StreamEntry, IBatchContainer> _dataAdapter;
    private RedisStreamStorage? _streamStorage;
    private readonly QueueId _queueId;
    private readonly ILogger<RedisStreamAdapterReceiver> _logger;

    private readonly List<RedisStreamPendingMessage> _pendingMessages = [];

    private Task? _outstandingTask;
    private long _lastSequenceId;

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
        _dataAdapter = dataAdapter;
        _streamStorage = streamStorage;
        _queueId = queueId;
        _logger = logger;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        if (_streamStorage != null) // check in case we already shut it down.
        {
            await _streamStorage.InitializeAsync();
        }
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            // await the last storage operation, so after we shutdown and stop this receiver we don't get async operation completions from pending storage operations.
            if (_outstandingTask != null)
                await _outstandingTask;
        }
        finally
        {
            // remember that we shut down so we never try to read from the queue again.
            _streamStorage = null;
        }
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        try
        {
            var streamStorageRef = _streamStorage; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
            if (streamStorageRef == null)
                return [];

            var count = maxCount is < 0 or QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG
                ? RedisStreamStorage.MaxNumberOfMsgToGet
                : Math.Min(maxCount, RedisStreamStorage.MaxNumberOfMsgToGet);

            var task = streamStorageRef
                .GetEntriesAsync(count);

            _outstandingTask = task;

            var streamEntries = await task;

            var messagesBatch = new List<IBatchContainer>();
            foreach (var streamEntry in streamEntries)
            {
                var container = _dataAdapter.FromQueueMessage(streamEntry, _lastSequenceId++);
                messagesBatch.Add(container);

                _pendingMessages.Add(new RedisStreamPendingMessage(streamEntry, container.SequenceToken));
            }

            return messagesBatch;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from stream {QueueId}", _queueId);
            return [];
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        try
        {
            var streamStorageRef = _streamStorage; // store direct ref, in case we are somehow asked to shutdown while we are receiving.            
            if (messages.Count == 0 || streamStorageRef == null)
                return;

            // get sequence tokens of delivered messages
            var deliveredTokens = messages.Select(m => m.SequenceToken).ToList();

            // find most recent (newest) delivered message token
            var newestToken = deliveredTokens.Max();

            // select all pending messages at or befor the newest delivered token
            var pendingMessagesToRemove = _pendingMessages
                .Where(pendingMessage => !pendingMessage.Token.Newer(newestToken))
                .ToList();

            if (pendingMessagesToRemove.Count == 0)
                return;

            // remove all pending messages at or befor the oldest token from pending, regardless of if it was acknowledge or not.
            foreach (var pendingMessage in pendingMessagesToRemove)
                _pendingMessages.Remove(pendingMessage);

            // get the stream entries for all messages deliveries that were delivered.
            var pendingMessagesStreamEntries = pendingMessagesToRemove
                .Where(pendingMessage => deliveredTokens.Contains(pendingMessage.Token))
                .Select(pendingMessage => pendingMessage.StreamEntry)
                .ToList();

            if (pendingMessagesStreamEntries.Count == 0)
                return;

            // Acknowledge all delivered messages.
            _outstandingTask = streamStorageRef.EntriesAcknowledgeAsync(pendingMessagesStreamEntries);
            try
            {
                await _outstandingTask;
            }
            catch (Exception exc)
            {
                LogWarningOperationException(_logger, exc, nameof(streamStorageRef.EntriesAcknowledgeAsync), _queueId);
            }
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception upon {Operation} on queue {QueueId}. Ignoring."
    )]
    private static partial void LogWarningOperationException(ILogger logger, Exception exception, string operation, QueueId queueId);
}
