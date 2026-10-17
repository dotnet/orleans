using Orleans.Internal;

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
internal partial class AdoNetQueueAdapterReceiver(string providerId, string queueId, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, IStreamMessageQueries queries, AdoNetStreamPositions positions, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapterReceiver> logger) : IQueueAdapterReceiver
{
    private readonly ILogger<AdoNetQueueAdapterReceiver> _logger = logger;
    private readonly AdmissionGate _admissionGate = new();
    private readonly object _lock = new();
    private readonly Dictionary<long, PendingMessage> _pendingMessages = [];

    public AdoNetQueueAdapterReceiver(
        string providerId,
        string queueId,
        AdoNetStreamOptions streamOptions,
        ClusterOptions clusterOptions,
        SimpleQueueCacheOptions cacheOptions,
        RelationalOrleansQueries queries,
        AdoNetStreamPositions positions,
        Serializer<AdoNetBatchContainer> serializer,
        ILogger<AdoNetQueueAdapterReceiver> logger)
        : this(providerId, queueId, streamOptions, clusterOptions, cacheOptions, new RelationalStreamMessageQueries(queries), positions, serializer, logger)
    {
    }

    /// <summary>
    /// Completes initialization immediately.
    /// </summary>
    public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

    /// <summary>
    /// Closes admission, drains accepted operations, and releases pending messages.
    /// </summary>
    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            await _admissionGate.CloseAsync().WaitAsync(timeout);
        }
        catch (Exception ex)
        {
            LogShutdownFault(ex, clusterOptions.ServiceId, providerId, queueId);
            return;
        }

        List<AdoNetStreamConfirmation> pending;
        lock (_lock)
        {
            RemoveExpiredPendingMessages();
            if (_pendingMessages.Count == 0)
            {
                return;
            }

            pending = new(_pendingMessages.Count);
            foreach (var (messageId, message) in _pendingMessages)
            {
                positions.Remove(message.Position);
                pending.Add(new(messageId, message.Dequeued));
            }
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
        using var admission = _admissionGate.TryEnter();
        if (!admission.Entered)
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

            // the queue cache requires sequence tokens which increase across reads, so tokens are assigned positions rather than message identifiers
            var dequeued = new List<(AdoNetStreamMessage Message, long Position)>(messages.Count);
            lock (_lock)
            {
                RemoveExpiredPendingMessages();
                foreach (var message in messages.OrderBy(static x => x.MessageId))
                {
                    if (_pendingMessages.TryGetValue(message.MessageId, out var previous))
                    {
                        positions.Remove(previous.Position);
                    }

                    var position = positions.Add(message.MessageId);
                    _pendingMessages[message.MessageId] = new(message.Dequeued, message.ExpiresOn, position);
                    dequeued.Add((message, position));
                }
            }

            // convert the messages into standard batch containers
            return dequeued.Select(x => AdoNetBatchContainer.FromMessage(serializer, x.Message, x.Position)).Cast<IBatchContainer>().ToList();
        }
        catch (Exception ex)
        {
            LogDequeueFailed(ex, clusterOptions.ServiceId, providerId, queueId);
            throw;
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

        using var admission = _admissionGate.TryEnter();
        if (!admission.Entered)
        {
            return;
        }

        // get the identifiers for the messages to confirm and release the positions of messages which have left the cache
        var items = new List<AdoNetStreamConfirmation>(messages.Count);
        foreach (AdoNetBatchContainer message in messages)
        {
            positions.Remove(message.SequenceToken.SequenceNumber);
            items.Add(new(message.MessageId, message.Dequeued));
        }

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

    private void RemoveExpiredPendingMessages()
    {
        var now = DateTime.UtcNow;
        var expired = _pendingMessages
            .Where(message => message.Value.ExpiresOn <= now)
            .Select(static message => message.Key)
            .ToList();
        foreach (var messageId in expired)
        {
            if (_pendingMessages.Remove(messageId, out var message))
            {
                positions.Remove(message.Position);
            }
        }
    }

    private readonly record struct PendingMessage(int Dequeued, DateTime ExpiresOn, long Position);

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
