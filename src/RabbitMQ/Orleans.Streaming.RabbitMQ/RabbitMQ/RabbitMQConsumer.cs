using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streams;
using RabbitMQ.Stream.Client;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

public static class RabbitMQMessage
{
    public static string Format => "yyyyMMddHHmmssffff";
    public static string CreatedAtFieldName => "CreatedAt";
}

internal sealed class RabbitMQQueueProvider
{
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;
    private readonly string _providerName;
    private readonly RabbitMQClientOptions _rabbitMqClientOptions;
    private readonly HashRingBasedPartitionedStreamQueueMapper _explicitQueueMapper;

    public RabbitMQQueueProvider(
        RabbitMQStreamSystemProvider streamSystemProvider,
        string providerName,
        RabbitMQClientOptions rabbitMqClientOptions)
    {
        _streamSystemProvider = streamSystemProvider;
        _providerName = providerName;
        _rabbitMqClientOptions = rabbitMqClientOptions;
        if (rabbitMqClientOptions.QueueNames is { Count: > 0 } queueNames)
        {
            _explicitQueueMapper = new(queueNames, providerName);
        }
    }

    public async Task<string> CreateOrGetQueue(QueueId queueId, StreamSystem streamSystem = null)
    {
        var queueName = GetQueueName(queueId);
        streamSystem ??= await _streamSystemProvider.GetConsumerStream().ConfigureAwait(false);
        await streamSystem.CreateStream(_rabbitMqClientOptions.StreamOptions with { Name = queueName }).ConfigureAwait(false);
        return queueName;
    }

    internal string GetQueueName(QueueId queueId) =>
        _explicitQueueMapper is null ? $"{_providerName}-{queueId}" : _explicitQueueMapper.QueueToPartition(queueId);
}

internal sealed class RabbitMQConsumer
{
    private readonly ConcurrentQueue<RawMessage> _messages = new();
    private readonly CancellationTokenSource _bufferCancellation = new();
    private readonly SemaphoreSlim _bufferSlots;
    private readonly SemaphoreSlim _dequeueLock = new(1);
    private readonly object _lock = new();
    private readonly ILogger<RabbitMQConsumer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly QueueId _queueId;
    private readonly RabbitMQQueueProvider _rabbitMqQueueProvider;
    private readonly Serializer<RabbitMqBatchContainer> _serializer;
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;
    private IConsumer _consumer;
    private Task<IConsumer> _consumerTask;
    private string _queueName;
    private long _lastReceivedOffset = -1;
    private bool _stopping = true;

    public RabbitMQConsumer(
        RabbitMQQueueProvider rabbitMqQueueProvider,
        RabbitMQStreamSystemProvider streamSystemProvider,
        ILoggerFactory loggerFactory,
        QueueId queueId,
        Serializer<RabbitMqBatchContainer> serializer,
        RabbitMqQueueCacheOptions cacheOptions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheOptions.CacheSize, 1);
        _streamSystemProvider = streamSystemProvider;
        _loggerFactory = loggerFactory;
        _queueId = queueId;
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<RabbitMQConsumer>();
        _rabbitMqQueueProvider = rabbitMqQueueProvider;
        _bufferSlots = new SemaphoreSlim(cacheOptions.CacheSize, cacheOptions.CacheSize);
    }

    public async Task CloseConsumer()
    {
        IConsumer consumer;
        lock (_lock)
        {
            _stopping = true;
            _bufferCancellation.Cancel();
            consumer = _consumer;
            _consumer = null;
            _consumerTask = null;
            _lastReceivedOffset = -1;
        }

        await _dequeueLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var bufferedMessageCount = _messages.Count;
            _messages.Clear();
            if (bufferedMessageCount > 0)
            {
                _bufferSlots.Release(bufferedMessageCount);
            }
        }
        finally
        {
            _dequeueLock.Release();
        }

        if (consumer is null)
        {
            return;
        }

        _logger.LogInformation("Stopping reading from RabbitMQ queue {QueueName}", _queueName);
        await consumer.Close().ConfigureAwait(false);
        _logger.LogInformation("{QueueName} consumer is not consuming messages anymore", _queueName);
    }

    public async Task StartConsumingMessages()
    {
        StartBuffering();
        await EnsureConsumer().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RabbitMqBatchContainer>> DequeueMessages(int maxCount)
    {
        await EnsureConsumer().ConfigureAwait(false);
        await _dequeueLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var rawMessages = _messages.Take(maxCount).ToList();
            var messages = new List<RabbitMqBatchContainer>(rawMessages.Count);
            foreach (var message in rawMessages)
            {
                messages.Add(RabbitMqBatchContainer.FromRabbit(
                    _serializer,
                    new ReadOnlySequence<byte>(message.Body),
                    message.CreatedAt,
                    message.Offset));
            }

            var removedCount = 0;
            try
            {
                foreach (var message in rawMessages)
                {
                    if (!_messages.TryDequeue(out var dequeued) || !ReferenceEquals(message, dequeued))
                    {
                        throw new InvalidOperationException("RabbitMQ consumer message queue ordering was corrupted.");
                    }

                    removedCount++;
                }
            }
            finally
            {
                if (removedCount > 0)
                {
                    _bufferSlots.Release(removedCount);
                }
            }

            return messages;
        }
        finally
        {
            _dequeueLock.Release();
        }
    }

    public async Task UpdateOffset(ulong newOffset)
    {
        var consumer = await EnsureConsumer().ConfigureAwait(false);
        await consumer.StoreOffset(newOffset).ConfigureAwait(false);
    }

    private async Task<IConsumer> EnsureConsumer()
    {
        Task<IConsumer> task;
        lock (_lock)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("The RabbitMQ consumer is stopped.");
            }

            if (_consumer is not null)
            {
                return _consumer;
            }

            task = _consumerTask ??= CreateConsumer();
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_consumerTask, task))
                {
                    _consumerTask = null;
                }
            }

            throw;
        }
    }

    private async Task<IConsumer> CreateConsumer()
    {
        var streamSystem = await _streamSystemProvider.GetConsumerStream().ConfigureAwait(false);
        _queueName = await _rabbitMqQueueProvider.CreateOrGetQueue(_queueId, streamSystem).ConfigureAwait(false);
        var initialOffset = GetResumeOffset(
            await GetOffset(streamSystem, _queueName, _queueName).ConfigureAwait(false));

        IConsumer createdConsumer = null;
        var connectionClosed = false;

        _logger.LogInformation("Creating consumer for {QueueName} at offset {Offset}", _queueName, initialOffset);
        var config = new RawConsumerConfig(_queueName)
        {
            OffsetSpec = new OffsetTypeOffset(initialOffset),
            Reference = _queueName,
            IsSingleActiveConsumer = true,
            ConsumerUpdateListener = async (reference, stream, isActive) =>
            {
                if (!isActive)
                {
                    return new OffsetTypeOffset(initialOffset);
                }

                var currentStreamSystem = await _streamSystemProvider.GetConsumerStream().ConfigureAwait(false);
                var activationOffset = GetResumeOffset(
                    await GetOffset(currentStreamSystem, reference, stream).ConfigureAwait(false));
                return new OffsetTypeOffset(activationOffset);
            },
            MessageHandler = (_, context, message) => BufferMessage(message, context.Offset),
            ConnectionClosedHandler = _ =>
            {
                Volatile.Write(ref connectionClosed, true);
                InvalidateConsumer(createdConsumer);
                return Task.CompletedTask;
            },
            MetadataHandler = update => _logger.LogInformation(
                "RabbitMQ metadata update {Code} received for {Stream}", update.Code, update.Stream)
        };

        createdConsumer = await streamSystem.CreateRawConsumer(
            config,
            _loggerFactory.CreateLogger<IConsumer>()).ConfigureAwait(false);

        lock (_lock)
        {
            if (Volatile.Read(ref connectionClosed))
            {
                throw new InvalidOperationException("The RabbitMQ consumer connection closed during initialization.");
            }

            if (_stopping)
            {
                _ = createdConsumer.Close();
                throw new InvalidOperationException("The RabbitMQ consumer was stopped during initialization.");
            }

            _consumer = createdConsumer;
        }

        _logger.LogInformation("Consumer created, now consuming {QueueName}", _queueName);
        return createdConsumer;
    }

    private async Task<ulong> GetOffset(StreamSystem streamSystem, string reference, string stream)
    {
        _logger.LogInformation("Retrieving last offset for {Consumer} stream", stream);
        try
        {
            var initialOffset = await streamSystem.QueryOffset(reference, stream).ConfigureAwait(false);
            _logger.LogInformation(
                "The {QueueName} consumer will resume consuming from message offset {Offset}",
                stream,
                initialOffset);
            return initialOffset;
        }
        catch (OffsetNotFoundException)
        {
            _logger.LogInformation("There is no offset for {StreamName} yet, will start consuming from 0", stream);
            return 0;
        }
    }

    internal ulong GetResumeOffset(ulong storedOffset)
    {
        lock (_lock)
        {
            return _lastReceivedOffset >= 0 && (ulong)_lastReceivedOffset >= storedOffset
                ? (ulong)_lastReceivedOffset + 1
                : storedOffset;
        }
    }

    private void InvalidateConsumer(IConsumer consumer)
    {
        lock (_lock)
        {
            if (consumer is null || ReferenceEquals(_consumer, consumer))
            {
                _consumer = null;
                _consumerTask = null;
            }
        }
    }

    internal int BufferedMessageCount => _messages.Count;

    internal void StartBuffering()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_bufferCancellation.IsCancellationRequested, this);
            _stopping = false;
        }
    }

    internal async Task BufferMessage(byte[] body, string createdAt, ulong offset)
    {
        await WaitForBufferSlot().ConfigureAwait(false);
        EnqueueBufferedMessage(new RawMessage(body, createdAt, offset));
    }

    private async Task BufferMessage(Message message, ulong offset)
    {
        await WaitForBufferSlot().ConfigureAwait(false);
        object createdAt = null;
        message.ApplicationProperties?.TryGetValue(RabbitMQMessage.CreatedAtFieldName, out createdAt);
        EnqueueBufferedMessage(new RawMessage(message.Data.Contents.ToArray(), createdAt?.ToString(), offset));
    }

    private async Task WaitForBufferSlot()
    {
        try
        {
            await _bufferSlots.WaitAsync(_bufferCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_bufferCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("The RabbitMQ consumer is closing.", _bufferCancellation.Token);
        }
    }

    private void EnqueueBufferedMessage(RawMessage message)
    {
        lock (_lock)
        {
            if (_stopping)
            {
                _bufferSlots.Release();
                throw new OperationCanceledException("The RabbitMQ consumer is closing.", _bufferCancellation.Token);
            }

            _messages.Enqueue(message);
            _lastReceivedOffset = checked((long)message.Offset);
        }
    }

    private sealed record RawMessage(byte[] Body, string CreatedAt, ulong Offset);
}
