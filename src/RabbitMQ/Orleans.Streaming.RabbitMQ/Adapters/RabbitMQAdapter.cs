using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters;

internal class RabbitMQAdapter : IQueueAdapter
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<QueueId, RabbitMQProducer> _producers = new();
    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly RabbitMQQueueProvider _rabbitMqQueueProvider;
    private readonly Serializer<RabbitMqBatchContainer> _rabbitMqContainerSerializer;
    private readonly RabbitMQAdapterReceiverFactory _receiverFactory;
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;

    public RabbitMQAdapter(HashRingBasedStreamQueueMapper streamQueueMapper,
        RabbitMQQueueProvider rabbitMqQueueProvider,
        RabbitMQStreamSystemProvider streamSystemProvider, ILoggerFactory loggerFactory,
        RabbitMQAdapterReceiverFactory receiverFactory, Serializer serializer, string providerName)
    {
        Name = providerName;
        _streamQueueMapper = streamQueueMapper;
        _rabbitMqQueueProvider = rabbitMqQueueProvider;
        _streamSystemProvider = streamSystemProvider;
        _loggerFactory = loggerFactory;
        _receiverFactory = receiverFactory;
        _rabbitMqContainerSerializer = serializer.GetSerializer<RabbitMqBatchContainer>();
    }

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        var queueId = _streamQueueMapper.GetQueueForStream(streamId);
        var producer = _producers.GetOrAdd(queueId,
            _ => new RabbitMQProducer(_streamSystemProvider, _rabbitMqQueueProvider, queueId));

        await producer.SendMessage(RabbitMqBatchContainer.ToRabbitMqMessage(_rabbitMqContainerSerializer,
            streamId,
            events,
            requestContext)).ConfigureAwait(false);
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        _receiverFactory.Create(new RabbitMQConsumer(_rabbitMqQueueProvider, _streamSystemProvider,
            _loggerFactory, queueId,
            _rabbitMqContainerSerializer), queueId);

    public string Name { get; }
    public bool IsRewindable => true;
    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;
}