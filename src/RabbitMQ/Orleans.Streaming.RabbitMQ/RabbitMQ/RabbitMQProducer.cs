using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using RabbitMQ.Stream.Client;
using RabbitMQ.Stream.Client.AMQP;
using RabbitMQ.Stream.Client.Reliable;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

internal sealed class RabbitMQProducer : IAsyncDisposable
{
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;
    private readonly RabbitMQQueueProvider _rabbitMqQueueProvider;
    private readonly QueueId _queueId;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Message, TaskCompletionSource<ConfirmationStatus>> _confirmations =
        new(ReferenceEqualityComparer.Instance);
    private Producer _producer;
    private Task<Producer> _producerCreatingTask;
    private bool _disposed;

    public RabbitMQProducer(
        RabbitMQStreamSystemProvider streamSystemProvider,
        RabbitMQQueueProvider rabbitMqQueueProvider,
        QueueId queueId,
        ILoggerFactory loggerFactory)
    {
        _streamSystemProvider = streamSystemProvider;
        _rabbitMqQueueProvider = rabbitMqQueueProvider;
        _queueId = queueId;
        _loggerFactory = loggerFactory;
    }

    public async Task SendMessage(byte[] messageBody)
    {
        var producer = await GetProducer().ConfigureAwait(false);
        var message = new Message(messageBody)
        {
            ApplicationProperties = new ApplicationProperties
            {
                { RabbitMQMessage.CreatedAtFieldName, DateTime.UtcNow.ToString(RabbitMQMessage.Format) }
            }
        };
        var confirmation = new TaskCompletionSource<ConfirmationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_confirmations.TryAdd(message, confirmation))
            {
                throw new InvalidOperationException("A RabbitMQ message confirmation was already registered.");
            }
        }

        try
        {
            await producer.Send(message).ConfigureAwait(false);
            var status = await confirmation.Task.ConfigureAwait(false);
            switch (status)
            {
                case ConfirmationStatus.Confirmed:
                    return;
                case ConfirmationStatus.ClientTimeoutError:
                    throw new TimeoutException("RabbitMQ did not confirm the published message before the client timeout.");
                default:
                    throw new InvalidOperationException($"RabbitMQ rejected the published message with status {status}.");
            }
        }
        finally
        {
            _confirmations.TryRemove(message, out _);
        }
    }

    private async Task<Producer> GetProducer()
    {
        Task<Producer> creationTask;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_producer is not null && _producer.IsOpen())
            {
                return _producer;
            }

            if (_producer is not null)
            {
                _producer = null;
                _producerCreatingTask = null;
            }

            creationTask = _producerCreatingTask ??= CreateProducer();
        }

        try
        {
            return await creationTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                if (ReferenceEquals(_producerCreatingTask, creationTask))
                {
                    _producerCreatingTask = null;
                }
            }

            throw;
        }
    }

    private async Task<Producer> CreateProducer()
    {
        var streamSystem = await _streamSystemProvider.GetProducerStream().ConfigureAwait(false);
        var queueName = await _rabbitMqQueueProvider.CreateOrGetQueue(_queueId, streamSystem).ConfigureAwait(false);
        var producer = await Producer.Create(
            new ProducerConfig(streamSystem, queueName)
            {
                ConfirmationHandler = HandleConfirmation
            },
            _loggerFactory.CreateLogger<Producer>()).ConfigureAwait(false);

        lock (_lock)
        {
            if (_disposed)
            {
                _ = producer.Close();
                throw new ObjectDisposedException(nameof(RabbitMQProducer));
            }

            _producer = producer;
        }

        return producer;
    }

    private Task HandleConfirmation(MessagesConfirmation confirmation)
    {
        foreach (var message in confirmation.Messages)
        {
            if (_confirmations.TryRemove(message, out var completion))
            {
                completion.TrySetResult(confirmation.Status);
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Producer producer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            producer = _producer;
            _producer = null;
            _producerCreatingTask = null;
        }

        foreach (var completion in _confirmations.Values)
        {
            completion.TrySetCanceled();
        }

        _confirmations.Clear();
        if (producer is not null)
        {
            await producer.Close().ConfigureAwait(false);
        }
    }
}
