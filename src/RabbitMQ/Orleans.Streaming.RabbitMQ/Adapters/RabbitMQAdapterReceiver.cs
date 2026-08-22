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
    private readonly Func<Task> _closeConsumer;
    private readonly Func<ulong, Task> _updateOffset;
    private readonly object _checkpointLock = new();
    private readonly SemaphoreSlim _checkpointWriteLock = new(1, 1);
    private readonly CancellationTokenSource _checkpointCancellation = new();
    private DateTime _initializationTime;
    private ulong? _pendingOffset;
    private Task _checkpointTask;
    private int _receiverState = ReceiverShutdown;
    private readonly object _initializationLock = new();
    private Task _initializationTask;

    public RabbitMQAdapterReceiver(RabbitMQConsumer rabbitConsumer,
        IQueueAdapterReceiverMonitor monitor, ILogger<RabbitMQAdapterReceiver> logger,
        RabbitMQClientOptions rabbitMqClientOptions,
        Func<ulong, Task> updateOffset = null,
        Func<Task> closeConsumer = null)
    {
        _rabbitConsumer = rabbitConsumer;
        _monitor = monitor;
        _logger = logger;
        _rabbitMqClientOptions = rabbitMqClientOptions;
        _updateOffset = updateOffset ?? (offset => rabbitConsumer.UpdateOffset(offset));
        _closeConsumer = closeConsumer ?? (() => rabbitConsumer.CloseConsumer());
    }


    public async Task Initialize(TimeSpan timeout)
    {
        _logger.LogInformation("Initializing RabbitMQ Receiver");

        Interlocked.Exchange(ref _receiverState, ReceiverRunning);
        await EnsureInitialized().ConfigureAwait(false);
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        if (_receiverState == ReceiverShutdown)
        {
            return new List<IBatchContainer>();
        }

        await EnsureInitialized().ConfigureAwait(false);
        var messages = await DequeueRabbitMessages(maxCount).ConfigureAwait(false);

        TrackMessagesReceived(messages);

        return messages.Cast<IBatchContainer>().ToList();
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var newOffset = (ulong)messages.Max(m => m.SequenceToken.SequenceNumber) + 1;
        lock (_checkpointLock)
        {
            if (_pendingOffset is null || newOffset > _pendingOffset)
            {
                _pendingOffset = newOffset;
            }
        }

        if (_rabbitMqClientOptions.IntervalToUpdateOffset <= TimeSpan.Zero)
        {
            await FlushPendingCheckpoint().ConfigureAwait(false);
        }
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        Exception failure = null;
        try
        {
            _checkpointCancellation.Cancel();
            if (_checkpointTask is not null)
            {
                await _checkpointTask.ConfigureAwait(false);
            }

            await FlushPendingCheckpoint().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Interlocked.Exchange(ref _receiverState, ReceiverShutdown);
        try
        {
            await _closeConsumer().ConfigureAwait(false);
            lock (_initializationLock)
            {
                _initializationTask = null;
            }
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        watch.Stop();
        _monitor?.TrackShutdown(failure is null, watch.Elapsed, failure);
        if (failure is not null)
        {
            _logger.LogError(failure, "Failed to stop consumer");
            throw failure;
        }
    }

    private async Task RunCheckpointLoop()
    {
        using var timer = new PeriodicTimer(_rabbitMqClientOptions.IntervalToUpdateOffset);
        try
        {
            while (await timer.WaitForNextTickAsync(_checkpointCancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    await FlushPendingCheckpoint().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to store the RabbitMQ consumer offset");
                }
            }
        }
        catch (OperationCanceledException) when (_checkpointCancellation.IsCancellationRequested)
        {
        }
    }

    internal async Task FlushPendingCheckpoint()
    {
        await _checkpointWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ulong? offset;
            lock (_checkpointLock)
            {
                offset = _pendingOffset;
            }

            if (offset is null)
            {
                return;
            }

            _logger.LogInformation("Checkpointing RabbitMQ message offset {Offset}", offset);
            await _updateOffset(offset.Value).ConfigureAwait(false);

            lock (_checkpointLock)
            {
                if (_pendingOffset <= offset)
                {
                    _pendingOffset = null;
                }
            }
        }
        finally
        {
            _checkpointWriteLock.Release();
        }
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

        try
        {
            var messages = await _rabbitConsumer.DequeueMessages(maxCount).ConfigureAwait(false);
            watch.Stop();
            _monitor?.TrackRead(true, watch.Elapsed, null);
            return messages;
        }
        catch (Exception exception)
        {
            watch.Stop();
            _monitor?.TrackRead(false, watch.Elapsed, exception);
            throw;
        }
    }

    /// <summary>
    ///     Initializes the RabbitMQAdapterReceiver by starting consuming from the stream queue based on the last checkpoint
    ///     saved.
    ///     If the initialization fails, it will be retried on the next <see cref="GetQueueMessagesAsync(int)" /> call.
    /// </summary>
    private async Task EnsureInitialized()
    {
        Task initializationTask;
        lock (_initializationLock)
        {
            initializationTask = _initializationTask ??= InitializeCore();
        }

        try
        {
            await initializationTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_initializationLock)
            {
                if (ReferenceEquals(_initializationTask, initializationTask))
                {
                    _initializationTask = null;
                }
            }

            throw;
        }
    }

    private async Task InitializeCore()
    {
        var watch = Stopwatch.StartNew();

        try
        {
            await _rabbitConsumer.StartConsumingMessages().ConfigureAwait(false);
            _initializationTime = DateTime.UtcNow;
            if (_rabbitMqClientOptions.IntervalToUpdateOffset > TimeSpan.Zero)
            {
                _checkpointTask = RunCheckpointLoop();
            }

            watch.Stop();
            _monitor.TrackInitialization(true, watch.Elapsed, null);
        }
        catch (Exception ex)
        {
            watch.Stop();
            _monitor.TrackInitialization(false, watch.Elapsed, ex);
            throw;
        }
    }
}