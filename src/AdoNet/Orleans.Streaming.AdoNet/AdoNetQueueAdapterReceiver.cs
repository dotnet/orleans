namespace Orleans.Streaming.AdoNet;

internal interface IStreamMessageQueries
{
    Task<IList<AdoNetStreamMessage>> GetStreamMessagesAsync(
        string serviceId,
        string providerId,
        string queueId,
        int maxCount,
        int maxAttempts,
        int visibilityTimeout,
        int removalTimeout,
        int evictionInterval,
        int evictionBatchSize);

    Task<IList<AdoNetStreamConfirmationAck>> ConfirmStreamMessagesAsync(
        string serviceId,
        string providerId,
        string queueId,
        IList<AdoNetStreamConfirmation> messages);

    Task<IList<AdoNetStreamConfirmationAck>> ReleaseStreamMessagesAsync(
        string serviceId,
        string providerId,
        string queueId,
        IList<AdoNetStreamConfirmation> messages);
}

/// <summary>
/// Receives message batches from an individual queue of an ADO.NET provider.
/// </summary>
internal partial class AdoNetQueueAdapterReceiver(string providerId, string queueId, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, IStreamMessageQueries queries, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapterReceiver> logger) : IQueueAdapterReceiver
{
    private readonly ILogger<AdoNetQueueAdapterReceiver> _logger = logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, PendingMessage> _pendingMessages = [];

    /// <summary>
    /// Flags that no further work should be attempted.
    /// </summary>
    private bool _shutdown;

    private int _activeOperations;
    private TaskCompletionSource? _operationsCompleted;

    public AdoNetQueueAdapterReceiver(
        string providerId,
        string queueId,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        SimpleQueueCacheOptions cacheOptions,
        RelationalOrleansQueries queries,
        Serializer<AdoNetBatchContainer> serializer,
        ILogger<AdoNetQueueAdapterReceiver> logger)
        : this(providerId, queueId, streamOptions, clusterOptions, cacheOptions, new RelationalStreamMessageQueries(queries), serializer, logger)
    {
    }

    /// <summary>
    /// This receiver does not require initialization.
    /// </summary>
    public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

    /// <summary>
    /// Waits for any outstanding work before shutting down.
    /// </summary>
    public async Task Shutdown(TimeSpan timeout)
    {
        Task? operationsCompleted;
        lock (_lock)
        {
            _shutdown = true;
            operationsCompleted = _operationsCompleted?.Task;
        }

        if (operationsCompleted is not null)
        {
            try
            {
                await operationsCompleted.WaitAsync(timeout);
            }
            catch (Exception ex)
            {
                LogShutdownFault(ex, clusterOptions.ServiceId, providerId, queueId);
                return;
            }
        }

        List<AdoNetStreamConfirmation> pending;
        lock (_lock)
        {
            RemoveExpiredPendingMessages();
            if (_pendingMessages.Count == 0)
            {
                return;
            }

            pending = _pendingMessages
                .Select(static item => new AdoNetStreamConfirmation(item.Key, item.Value.Dequeued))
                .ToList();
        }

        try
        {
            var released = await queries.ReleaseStreamMessagesAsync(clusterOptions.ServiceId, providerId, queueId, pending).WaitAsync(timeout);
            lock (_lock)
            {
                foreach (var message in released)
                {
                    _pendingMessages.Remove(message.MessageId);
                }
            }
        }
        catch (Exception ex)
        {
            LogReleaseFailed(ex, clusterOptions.ServiceId, providerId, queueId, pending);
        }
    }

    /// <inheritdoc />
    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        if (!TryBeginOperation())
        {
            return [];
        }

        // cap max count as appropriate
        maxCount = Math.Min(maxCount, cacheOptions.CacheSize);

        try
        {
            var messages = await queries.GetStreamMessagesAsync(
                clusterOptions.ServiceId,
                providerId,
                queueId,
                maxCount,
                streamOptions.MaxAttempts,
                streamOptions.VisibilityTimeout.TotalSecondsCeiling(),
                streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(),
                streamOptions.EvictionInterval.TotalSecondsCeiling(),
                streamOptions.EvictionBatchSize);

            lock (_lock)
            {
                RemoveExpiredPendingMessages();
                foreach (var message in messages)
                {
                    _pendingMessages[message.MessageId] = new(message.Dequeued, message.ExpiresOn);
                }
            }

            // convert the messages into standard batch containers
            return messages.Select(x => AdoNetBatchContainer.FromMessage(serializer, x)).Cast<IBatchContainer>().ToList();
        }
        catch (Exception ex)
        {
            LogDequeueFailed(ex, clusterOptions.ServiceId, providerId, queueId);
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <inheritdoc />
    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        // skip work if there are no messages to deliver
        if (messages.Count == 0)
        {
            return;
        }

        if (!TryBeginOperation())
        {
            return;
        }

        // get the identifiers for the messages to confirm
        var items = messages.Cast<AdoNetBatchContainer>().Select(x => new AdoNetStreamConfirmation(x.SequenceToken.SequenceNumber, x.Dequeued)).ToList();

        try
        {
            try
            {
                var confirmed = await queries.ConfirmStreamMessagesAsync(clusterOptions.ServiceId, providerId, queueId, items);
                var receipts = items.ToDictionary(static item => item.MessageId, static item => item.Dequeued);
                lock (_lock)
                {
                    foreach (var message in confirmed)
                    {
                        if (receipts.TryGetValue(message.MessageId, out var receipt)
                            && _pendingMessages.TryGetValue(message.MessageId, out var pending)
                            && receipt == pending.Dequeued)
                        {
                            _pendingMessages.Remove(message.MessageId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogConfirmationFailed(ex, clusterOptions.ServiceId, providerId, queueId, items);
                throw;
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBeginOperation()
    {
        lock (_lock)
        {
            if (_shutdown)
            {
                return false;
            }

            if (_activeOperations++ == 0)
            {
                _operationsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return true;
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? operationsCompleted = null;
        lock (_lock)
        {
            if (--_activeOperations == 0)
            {
                operationsCompleted = _operationsCompleted;
                _operationsCompleted = null;
            }
        }

        operationsCompleted?.TrySetResult();
    }

    private void RemoveExpiredPendingMessages()
    {
        var now = DateTime.UtcNow;
        var expired = _pendingMessages
            .Where(message => message.Value.ExpiresOn <= now)
            .Select(static message => message.Key)
            .ToList();
        foreach (var messageId in expired)
        {
            _pendingMessages.Remove(messageId);
        }
    }

    private readonly record struct PendingMessage(int Dequeued, DateTime ExpiresOn);

    private sealed class RelationalStreamMessageQueries(RelationalOrleansQueries queries) : IStreamMessageQueries
    {
        public Task<IList<AdoNetStreamMessage>> GetStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            int maxCount,
            int maxAttempts,
            int visibilityTimeout,
            int removalTimeout,
            int evictionInterval,
            int evictionBatchSize) =>
            queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        public Task<IList<AdoNetStreamConfirmationAck>> ConfirmStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages) =>
            queries.ConfirmStreamMessagesAsync(serviceId, providerId, queueId, messages);

        public Task<IList<AdoNetStreamConfirmationAck>> ReleaseStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages) =>
            queries.ReleaseStreamMessagesAsync(serviceId, providerId, queueId, messages);
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to get messages from ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogDequeueFailed(Exception exception, string serviceId, string providerId, string queueId);

    [LoggerMessage(2, LogLevel.Error, "Failed to confirm messages for ({ServiceId}, {ProviderId}, {QueueId}, {@Items})")]
    private partial void LogConfirmationFailed(Exception exception, string serviceId, string providerId, string queueId, List<AdoNetStreamConfirmation> items);

    [LoggerMessage(3, LogLevel.Warning, "Handled fault while shutting down receiver for ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogShutdownFault(Exception exception, string serviceId, string providerId, string queueId);

    [LoggerMessage(4, LogLevel.Warning, "Failed to release messages while shutting down receiver for ({ServiceId}, {ProviderId}, {QueueId}, {@Items})")]
    private partial void LogReleaseFailed(Exception exception, string serviceId, string providerId, string queueId, List<AdoNetStreamConfirmation> items);

    #endregion Logging
}
