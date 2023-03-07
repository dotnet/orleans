using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters;

internal class RabbitMQAdapterReceiverFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly RabbitMQClientOptions _rabbitMqClientOptions;
    private readonly Serializer _serializer;

    public RabbitMQAdapterReceiverFactory(
        ILoggerFactory loggerFactory, Serializer serializer, RabbitMQClientOptions rabbitMqClientOptions)
    {
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        _rabbitMqClientOptions = rabbitMqClientOptions;
    }

    public RabbitMQAdapterReceiver Create(RabbitMQConsumer rabbitMqConsumer, QueueId queueId)
        => new(rabbitMqConsumer,
            new DefaultQueueAdapterReceiverMonitor(new ReceiverMonitorDimensions(queueId.ToString())),
            _loggerFactory.CreateLogger<RabbitMQAdapterReceiver>(), _rabbitMqClientOptions);
}