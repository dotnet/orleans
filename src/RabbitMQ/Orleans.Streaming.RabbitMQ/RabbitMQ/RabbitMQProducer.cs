using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;
using RabbitMQ.Stream.Client;
using RabbitMQ.Stream.Client.AMQP;
using RabbitMQ.Stream.Client.Reliable;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

internal class RabbitMQProducer : IAsyncDisposable
{
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;
    private readonly RabbitMQQueueProvider _rabbitMqQueueProvider;
    private readonly QueueId _queueId;
    private Producer _producer;
    private object _lock = new();
    private Task<Producer> _producerCreatingTask;

    public RabbitMQProducer(RabbitMQStreamSystemProvider streamSystemProvider, RabbitMQQueueProvider rabbitMqQueueProvider, QueueId queueId)
    {
        _streamSystemProvider = streamSystemProvider;
        _rabbitMqQueueProvider = rabbitMqQueueProvider;
        _queueId = queueId;
    }

    public async Task SendMessage(byte[] messageBody)
    {
        var producer = await GetProducer().ConfigureAwait(false);

        await producer.Send(new Message(messageBody)
        {
            ApplicationProperties =
                new ApplicationProperties
                {
                    { RabbitMQMessage.CreatedAtFieldName, DateTime.UtcNow.ToString(RabbitMQMessage.Format) }
                }
        }).ConfigureAwait(false);
    }

    private async Task<Producer> GetProducer()
    {
        if (_producer is not null)
        {
            return _producer;
        }

        lock (_lock)
        {
            _producerCreatingTask ??= CreateProducer();
        }

        return await _producerCreatingTask.ConfigureAwait(false);
    }

    private async Task<Producer> CreateProducer()
    {
        var streamSystem = await _streamSystemProvider.GetConsumerStream().ConfigureAwait(false);
        var queueName = await _rabbitMqQueueProvider.CreateOrGetQueue(_queueId).ConfigureAwait(false);
        _producer = await Producer.Create(new ProducerConfig(streamSystem, queueName)).ConfigureAwait(false);

        return _producer;
    }

    public async ValueTask DisposeAsync()
    {
        _producerCreatingTask?.Dispose();

        if (_producer is not null)
            await _producer.Close().ConfigureAwait(false);
    }
}