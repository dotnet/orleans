using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ;
using Orleans.Streams;
using RabbitMQ.Client;

namespace Orleans.RabbitMQ.Providers
{
    // todo (mxplusb): update this.
    public class RabbitMQAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Orleans logging.
        /// </summary>
        protected ILogger _logger;

        /// <summary>
        /// Framework Service Provider
        /// </summary>
        protected IServiceProvider _serviceProvider;

        /// <summary>
        /// RabbitMQ Stream Provider settings.
        /// </summary>
        private RabbitMQOptions _rabbitOpts;
        private RabbitMQReceiverOptions _rabbitReceiverOpts;
        private StreamCacheEvictionOptions _cacheEvictionOpts;
        private IRabbitMQQueueMapper _rabbitQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, RabbitMQAdapterReceiver> _receivers;
        private IConnection _rabbitClient;
        private ITelemetryProducer _telemetryProducer;

        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; set; }

        /// <summary>
        /// Name of the adapter, for logging purposes.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Determines whether this is a rewindable stream adapter and supports subscribing from a previous point in time.
        /// </summary>
        public bool IsRewindable => false;

        /// <summary>
        /// Direction of this queue adapter.
        /// </summary>
        public StreamProviderDirection Direction { get; protected set; } = StreamProviderDirection.ReadWrite;

        /// <summary>
        /// Creates a message cache for a RabbitMQ topic parition.
        /// </summary>
        protected Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, ITelemetryProducer, IRabbitMQQueueCache> CacheFactory { get; set; }

        internal ConcurrentDictionary<QueueId, RabbitMQAdapterReceiver> RabbitMQReceivers { get { return _receivers; } }
        internal IRabbitMQQueueMapper EventHubQueueMapper { get { return _rabbitQueueMapper; } }

        public RabbitMQAdapterFactory(string name,
                                      RabbitMQOptions rabbitOpts,
                                      RabbitMQReceiverOptions rabbitReceiverOpts,
                                      ILoggerFactory loggerFactory,
                                      IServiceProvider serviceProvider,
                                      StreamCacheEvictionOptions cacheEvictionOpts,
                                      ITelemetryProducer telemetryProducer,
                                      SerializationManager serializationManager)
        {
            _rabbitOpts = rabbitOpts ?? throw new ArgumentNullException(nameof(rabbitOpts));
            _rabbitReceiverOpts = rabbitReceiverOpts ?? throw new ArgumentNullException(nameof(rabbitReceiverOpts));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _cacheEvictionOpts = cacheEvictionOpts ?? throw new ArgumentNullException(nameof(cacheEvictionOpts));
            _telemetryProducer = telemetryProducer ?? throw new ArgumentNullException(nameof(telemetryProducer));
            SerializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public virtual void Init()
        {
            _receivers = new ConcurrentDictionary<QueueId, RabbitMQAdapterReceiver>();
            _telemetryProducer = _serviceProvider.GetService<ITelemetryProducer>();

            if (CacheFactory == null)
            {
                CacheFactory = null;
            }
        }

        protected virtual IRabbitMQQueueCacheFactory CreateCacheFactory()
        {
            return new RabbitMQQueueCacheFactory();
        }


        public Task<IQueueAdapter> CreateAdapter()
        {
            throw new System.NotImplementedException();
        }

        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            throw new NotImplementedException();
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            throw new NotImplementedException();
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            throw new System.NotImplementedException();
        }

        public IQueueAdapterCache GetQueueAdapterCache()
        {
            throw new System.NotImplementedException();
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            throw new System.NotImplementedException();
        }

        public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            throw new NotImplementedException();
        }
    }
}
