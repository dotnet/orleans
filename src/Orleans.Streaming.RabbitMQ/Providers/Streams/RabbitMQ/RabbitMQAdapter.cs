using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    internal class RabbitMQAdapter<TDataAdapter> : IQueueAdapter
        where TDataAdapter : IRabbitMQDataAdapter
    {
        private TDataAdapter _dataAdapter;
        private SerializationManager _serializationManager;
        private ILoggerFactory _loggerFactory;
        private HashRingBasedStreamQueueMapper _queueMapper;
        private RabbitMQOptions _options;
        private string _serviceId;
        private string _providerName;

        public string Name { get; }
        public bool IsRewindable => false;
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        public RabbitMQAdapter(TDataAdapter dataAdapter,
                               SerializationManager serializationManager,
                               ILoggerFactory loggerFactory,
                               HashRingBasedStreamQueueMapper queueMapper,
                               RabbitMQOptions options,
                               string serviceId,
                               string providerName)
        {
            _dataAdapter = dataAdapter;
            _serializationManager = serializationManager;
            _loggerFactory = loggerFactory;
            _queueMapper = queueMapper;
            _options = options;
            _serviceId = serviceId;
            _providerName = providerName;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Sends messages to RabbitMQ.
        /// </summary>
        public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            var queueId = _queueMapper.GetQueueForStream(streamGuid, streamNamespace);
            var logger = _loggerFactory.CreateLogger($"{this.GetType().FullName}.{queueId}");
            var manager = new RabbitMQManager(_options, logger);
            var msg = _dataAdapter.ToRabbitMQMessage(streamGuid, streamNamespace, events, requestContext);
            manager.PublishMessage(msg);
            return Task.CompletedTask;
        }
    }
}