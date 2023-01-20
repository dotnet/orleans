using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters;

internal class RabbitMQAdapterReceiver : IQueueAdapterReceiver
{
    private const int ReceiverShutdown = 0;
    private const int ReceiverRunning = 1;
    private readonly ILogger<RabbitMQAdapterReceiver> _logger;
    private readonly IQueueAdapterReceiverMonitor _monitor;
    private readonly RabbitMQConsumer _rabbitConsumer;
    private readonly RabbitMQClientOptions _rabbitMqClientOptions;
    private DateTime _initializationTime;
    private DateTime? _lastOffsetUpdate;
    private int _messagesConsumedCount;

    private int _messagesDeliveredCount;

    private int _receiverState = ReceiverShutdown;

    public RabbitMQAdapterReceiver(RabbitMQConsumer rabbitConsumer,
        IQueueAdapterReceiverMonitor monitor, ILogger<RabbitMQAdapterReceiver> logger,
        RabbitMQClientOptions rabbitMqClientOptions)
    {
        _rabbitConsumer = rabbitConsumer;
        _monitor = monitor;
        _logger = logger;
        _rabbitMqClientOptions = rabbitMqClientOptions;
    }


    public async Task Initialize(TimeSpan timeout)
    {
        _logger.LogInformation("Initializing RabbitMQ Receiver");

        if (ReceiverRunning == Interlocked.Exchange(ref _receiverState, ReceiverRunning))
        {
            _logger.LogInformation(
                "Another initialization for this receiver instance is already in progress, cancelling");
            return;
        }

        await Initialize().ConfigureAwait(false);
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        //return Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>());

        if (_receiverState == ReceiverShutdown)
        {
            return new List<IBatchContainer>();
        }

        var messages = await DequeueRabbitMessages(maxCount).ConfigureAwait(false);

        TrackMessagesReceived(messages);

        return messages.Cast<IBatchContainer>().ToList();
    }

    //public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    //{
    //    return Task.FromResult<IList<IBatchContainer>>(new List<IBatchContainer>());

    //    //if (_receiverState == ReceiverShutdown)
    //    //{
    //    //    return new List<IBatchContainer>();
    //    //}

    //    //var messages = await DequeueRabbitMessages(maxCount).ConfigureAwait(false);

    //    //TrackMessagesReceived(messages);

    //    //return messages.Cast<IBatchContainer>().ToList();
    //}

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        Interlocked.Add(ref _messagesDeliveredCount, messages.Count);
        var messagesDeliveredCountAux = _messagesDeliveredCount;
        var messagesConsumedCountAux = _messagesConsumedCount;

        _logger.LogInformation($"Removing {messages.Count} messages from the queue these were already processed");

        if (messagesDeliveredCountAux >= messagesConsumedCountAux && IsTimeToUpdateOffset())
        {
            //RabbitMQ starts reading messages from the provided offset, different from Orleans streams, which start reading after the provided offset.
            //To skip the last message read, we need to update the offset to offset + 1
            var newOffset = (ulong)messages.Max(m => m.SequenceToken.SequenceNumber) + 1;
            await _rabbitConsumer.UpdateOffset(newOffset).ConfigureAwait(false);
            _lastOffsetUpdate = DateTime.UtcNow;
            Interlocked.Exchange(ref _messagesDeliveredCount, _messagesDeliveredCount - messagesDeliveredCountAux);
            Interlocked.Exchange(ref _messagesConsumedCount, _messagesConsumedCount - messagesConsumedCountAux);
        }
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            // if receiver was already shutdown, we can just leave
            if (ReceiverShutdown == Interlocked.Exchange(ref _receiverState, ReceiverShutdown))
            {
                return;
            }

            await _rabbitConsumer.CloseConsumer().ConfigureAwait(false);

            watch.Stop();
            _monitor?.TrackShutdown(true, watch.Elapsed, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop consumer");
            watch.Stop();
            _monitor?.TrackShutdown(false, watch.Elapsed, ex);
            throw;
        }
    }

    private bool IsTimeToUpdateOffset()
    {
        var lastOffsetUpdate = _lastOffsetUpdate ?? _initializationTime;

        return (DateTime.UtcNow - lastOffsetUpdate).TotalMilliseconds >=
               _rabbitMqClientOptions.IntervalToUpdateOffset.TotalMilliseconds;
    }

    private void TrackMessagesReceived(IReadOnlyList<RabbitMqBatchContainer> messages)
    {
        if (messages.Count == 0)
        {
            _monitor?.TrackMessagesReceived(0, null, null);
            return;
        }

        if (TryParseMessageCreatedAt(messages[0].CreatedAt, out var oldestMessageEnqueueTime) &&
            TryParseMessageCreatedAt(messages[^1].CreatedAt, out var newestMessageEnqueueTime))
        {
            _monitor?.TrackMessagesReceived(messages.Count, oldestMessageEnqueueTime, newestMessageEnqueueTime);
        }
    }

    private static bool TryParseMessageCreatedAt(string createdAt,
        out DateTime date) =>
        DateTime.TryParseExact(createdAt, RabbitMQMessage.Format, CultureInfo.CurrentCulture,
            DateTimeStyles.None, out date);

    private async Task<IReadOnlyList<RabbitMqBatchContainer>> DequeueRabbitMessages(int maxCount)
    {
        var watch = Stopwatch.StartNew();

        var messages = await _rabbitConsumer.DequeueMessages(maxCount).ConfigureAwait(false);
        Interlocked.Add(ref _messagesConsumedCount, messages.Count);
        watch.Stop();

        _monitor?.TrackRead(true, watch.Elapsed, null);
        return messages;
    }

    /// <summary>
    ///     Initializes the RabbitMQAdapterReceiver by starting consuming from the stream queue based on the last checkpoint
    ///     saved.
    ///     If the initialization fails, it will be retried on the next <see cref="GetQueueMessagesAsync(int)" /> call.
    /// </summary>
    private async Task Initialize()
    {
        var watch = Stopwatch.StartNew();

        try
        {
            await _rabbitConsumer.StartConsumingMessages().ConfigureAwait(false);
            _initializationTime = DateTime.UtcNow;
            watch.Stop();
            _monitor.TrackInitialization(false, watch.Elapsed, null);
        }
        catch (Exception ex)
        {
            watch.Stop();
            _monitor.TrackInitialization(false, watch.Elapsed, ex);
            throw;
        }
    }
}