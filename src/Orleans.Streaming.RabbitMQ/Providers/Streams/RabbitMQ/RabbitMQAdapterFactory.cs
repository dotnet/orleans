using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    public class RabbitMQAdapterFactory<TDataAdapter> : IQueueAdapterFactory where TDataAdapter : IRabbitMQDataAdapter
    {
        private readonly string _providerName;
        private readonly RabbitMQOptions _options;
        private readonly ClusterOptions _clusterOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly Func<TDataAdapter> _adapterFactory;
        private HashRingBasedStreamQueueMapper _queueMapper;
        private IQueueAdapterCache _queueAdapterCache;


        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public RabbitMQAdapterFactory(
            string name,
            RabbitMQOptions options,
            HashRingStreamQueueMapperOptions queueMapperOpts,
            SimpleQueueCacheOptions cacheOptions,
            IServiceProvider serviceProvider,
            IOptions<ClusterOptions> clusterOptions,
            SerializationManager serializationManager,
            ILoggerFactory loggerFactory)
        {
            _providerName = name;
            _options = options;
            _clusterOptions = clusterOptions.Value;
            this.SerializationManager = serializationManager;
            _loggerFactory = loggerFactory;
            _adapterFactory = () => ActivatorUtilities.GetServiceOrCreateInstance<TDataAdapter>(serviceProvider);
            _queueAdapterCache = new SimpleQueueAdapterCache(cacheOptions, _providerName, _loggerFactory);
            _queueMapper = new HashRingBasedStreamQueueMapper(queueMapperOpts, _providerName);
        }

        public virtual void Init()
        {
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new RabbitMQAdapter<TDataAdapter>(_adapterFactory(),
                                                            this.SerializationManager,
                                                            _loggerFactory,
                                                            _queueMapper,
                                                            _options,
                                                            _clusterOptions.ServiceId,
                                                            _providerName);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => StreamFailureHandlerFactory(queueId);

        public IQueueAdapterCache GetQueueAdapterCache() => _queueAdapterCache;

        public IStreamQueueMapper GetStreamQueueMapper() => _queueMapper;
    }
}
