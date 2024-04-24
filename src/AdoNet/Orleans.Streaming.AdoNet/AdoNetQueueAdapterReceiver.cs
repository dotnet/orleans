using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Receives message batches from an individual queue of an ADO.NET provider.
/// </summary>
internal partial class AdoNetQueueAdapterReceiver : IQueueAdapterReceiver
{
    public AdoNetQueueAdapterReceiver(string serviceId, string providerId, string queueId, AdoNetStreamOptions streamOptions, Serializer<AdoNetBatchContainer> serializer, ILogger<AdoNetQueueAdapterReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);
        ArgumentNullException.ThrowIfNull(streamOptions);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceId = serviceId;
        _providerId = providerId;
        _queueId = queueId;
        _streamOptions = streamOptions;
        _serializer = serializer;
        _logger = logger;
    }

    private readonly string _serviceId;
    private readonly string _providerId;
    private readonly string _queueId;
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly Serializer<AdoNetBatchContainer> _serializer;
    private readonly ILogger _logger;
    private RelationalOrleansQueries _queries;

    /// <summary>
    /// Flags that further work should be attempted.
    /// </summary>
    private bool _halt;

    /// <summary>
    /// Helps shutdown wait for any outstanding storage operation.
    /// </summary>
    private Task _outstandingTask = null;

    /// <summary>
    /// Initializes the receiver with the underlying storage queries.
    /// </summary>
    public async Task Initialize(TimeSpan timeout) => _queries = await RelationalOrleansQueries.CreateInstance(_streamOptions.Invariant, _streamOptions.ConnectionString).WaitAsync(timeout);

    /// <summary>
    /// This receiver does not need to shutdown.
    /// </summary>
    public async Task Shutdown(TimeSpan timeout)
    {
        // disable any further attempts to access storage
        _halt = true;

        // wait for any outstanding storage operation to complete.
        var outstandingTask = _outstandingTask;
        if (outstandingTask is not null)
        {
            await outstandingTask;
        }
    }

    /// <summary>
    /// Dequeues a batch of messages from the associated adonet queue.
    /// </summary>
    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        // if shutdown has been called we refuse to work gracefully
        if (_halt)
        {
            return [];
        }

        // cap max count as appropriate
        maxCount = maxCount <= 0 ? _streamOptions.MaxBatchSize : Math.Min(maxCount, _streamOptions.MaxBatchSize);

        try
        {
            // grab a message batch from storage while pinning the task so shutdown can wait for it
            var task = _queries.GetQueueMessagesAsync(_serviceId, _providerId, _queueId, maxCount, _streamOptions.MaxAttempts, _streamOptions.VisibilityTimeout);
            _outstandingTask = task;
            var messages = await task;

            // convert the messages into standard batch containers
            return messages.Select(x => AdoNetBatchContainer.FromMessage(_serializer, x)).Cast<IBatchContainer>().ToList();
        }
        catch (Exception ex)
        {
            LogDequeueFailed(ex, _serviceId, _providerId, _queueId);
            throw;
        }
        finally
        {
            _outstandingTask = null;
        }
    }

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
            var task = _queries.MessagesDeliveredAsync(_serviceId, _providerId, _queueId, items);
            _outstandingTask = task;

            try
            {
                await task;
            }
            catch (Exception ex)
            {
                LogConfirmationFailed(ex, _serviceId, _providerId, _queueId, items);
                throw;
            }
        }
        finally
        {
            _outstandingTask = null;
        }
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to dequeue messages for ServiceId: {ServiceId}, ProviderId: {ProviderId}, QueueId: {QueueId}")]
    private partial void LogDequeueFailed(Exception exception, string serviceId, string providerId, string queueId);

    [LoggerMessage(2, LogLevel.Error, "Failed to confirm messages for ServiceId: {ServiceId}, ProviderId: {ProviderId}, QueueId: {QueueId}, Items: {Items}")]
    private partial void LogConfirmationFailed(Exception exception, string serviceId, string providerId, string queueId, List<AdoNetStreamConfirmation> items);

    #endregion Logging
}