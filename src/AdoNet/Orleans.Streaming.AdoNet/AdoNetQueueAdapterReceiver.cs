namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Receives message batches from an individual queue of an ADO.NET provider.
/// </summary>
internal partial class AdoNetQueueAdapterReceiver(string providerId, string queueId, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, RelationalOrleansQueries queries, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapterReceiver> logger) : IQueueAdapterReceiver
{
    private readonly ILogger<AdoNetQueueAdapterReceiver> _logger = logger;

    /// <summary>
    /// Flags that no further work should be attempted.
    /// </summary>
    private bool _shutdown;

    /// <summary>
    /// Helps shutdown wait for any outstanding storage operation.
    /// </summary>
    private Task _outstandingTask;

    /// <summary>
    /// This receiver does not require initialization.
    /// </summary>
    public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

    /// <summary>
    /// Waits for any outstanding work before shutting down.
    /// </summary>
    public async Task Shutdown(TimeSpan timeout)
    {
        // disable any further attempts to access storage
        _shutdown = true;

        // wait for any outstanding storage operation to complete.
        var outstandingTask = _outstandingTask;
        if (outstandingTask is not null)
        {
            try
            {
                await outstandingTask.WaitAsync(timeout);
            }
            catch (Exception ex)
            {
                LogShutdownFault(ex, clusterOptions.ServiceId, providerId, queueId);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        // if shutdown has been called then we refuse further requests gracefully
        if (_shutdown)
        {
            return [];
        }

        // cap max count as appropriate
        maxCount = Math.Min(maxCount, cacheOptions.CacheSize);

        try
        {
            // grab a message batch from storage while pinning the task so shutdown can wait for it
            var task = queries.GetStreamMessagesAsync(
                clusterOptions.ServiceId,
                providerId,
                queueId,
                maxCount,
                streamOptions.MaxAttempts,
                streamOptions.VisibilityTimeout.TotalSecondsCeiling(),
                streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(),
                streamOptions.EvictionInterval.TotalSecondsCeiling(),
                streamOptions.EvictionBatchSize);

            _outstandingTask = task;

            var messages = await task;

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
            _outstandingTask = null;
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

        // get the identifiers for the messages to confirm
        var items = messages.Cast<AdoNetBatchContainer>().Select(x => new AdoNetStreamConfirmation(x.SequenceToken.SequenceNumber, x.Dequeued)).ToList();

        try
        {
            // execute the confirmation while pinning the task so shutdown can wait for it
            var task = queries.ConfirmStreamMessagesAsync(clusterOptions.ServiceId, providerId, queueId, items);
            _outstandingTask = task;

            try
            {
                await task;
            }
            catch (Exception ex)
            {
                LogConfirmationFailed(ex, clusterOptions.ClusterId, providerId, queueId, items);
                throw;
            }
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to get messages from ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogDequeueFailed(Exception exception, string serviceId, string providerId, string queueId);

    [LoggerMessage(2, LogLevel.Error, "Failed to confirm messages for ({ServiceId}, {ProviderId}, {QueueId}, {@Items})")]
    private partial void LogConfirmationFailed(Exception exception, string serviceId, string providerId, string queueId, List<AdoNetStreamConfirmation> items);

    [LoggerMessage(3, LogLevel.Warning, "Handled fault while shutting down receiver for ({ServiceId}, {ProviderId}, {QueueId})")]
    private partial void LogShutdownFault(Exception exception, string serviceId, string providerId, string queueId);

    #endregion Logging
}