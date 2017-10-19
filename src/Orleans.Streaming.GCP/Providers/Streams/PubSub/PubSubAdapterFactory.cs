using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Threading.Tasks;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    public class PubSubAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IPubSubDataAdapter
    {
        private string _projectId;
        private string _topicId;
        private string _deploymentId;
        private string _providerName;
        private string _customEndpoint;
        private int _cacheSize;
        private int _numSubscriptions;
        private TimeSpan? _deadline;
        private HashRingBasedStreamQueueMapper _streamQueueMapper;
        private IQueueAdapterCache _adapterCache;
        private Func<TDataAdapter> _adaptorFactory;
        private Logger _logger;

        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger, IServiceProvider serviceProvider)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!config.Properties.TryGetValue(PubSubAdapterConstants.PROJECT_ID, out _projectId))
                throw new ArgumentException($"{PubSubAdapterConstants.PROJECT_ID} property not set");
            if (!config.Properties.TryGetValue(PubSubAdapterConstants.TOPIC_ID, out _topicId))
                throw new ArgumentException($"{PubSubAdapterConstants.TOPIC_ID} property not set");
            if (!config.Properties.TryGetValue(PubSubAdapterConstants.DEPLOYMENT_ID, out _deploymentId))
                throw new ArgumentException($"{PubSubAdapterConstants.DEPLOYMENT_ID} property not set");

            _logger = logger;

            config.Properties.TryGetValue(PubSubAdapterConstants.CUSTOM_ENDPOINT, out _customEndpoint);

            string deadlineStr;
            if (config.Properties.TryGetValue(PubSubAdapterConstants.DEADLINE, out deadlineStr))
            {
                int seconds;
                if (!int.TryParse(deadlineStr, out seconds))
                {
                    throw new ArgumentException(
                        $"Failed to parse {PubSubAdapterConstants.DEADLINE} value '{deadlineStr}' as a TimeSpan");
                }

                _deadline = TimeSpan.FromSeconds(seconds);

                if (_deadline == TimeSpan.MinValue || _deadline > PubSubAdapterConstants.MAX_DEADLINE)
                    _deadline = PubSubAdapterConstants.MAX_DEADLINE;
            }
            else
            {
                _deadline = null;
            }

            _cacheSize = SimpleQueueAdapterCache.ParseSize(config, PubSubAdapterConstants.CACHE_SIZE_DEFAULT);

            string numSubscriptionsString;
            _numSubscriptions = PubSubAdapterConstants.NUMBER_SUBSCRIPTIONS_DEFAULT;
            if (config.Properties.TryGetValue(PubSubAdapterConstants.NUMBER_SUBSCRIPTIONS, out numSubscriptionsString))
            {
                if (!int.TryParse(numSubscriptionsString, out _numSubscriptions))
                    throw new ArgumentException($"{PubSubAdapterConstants.NUMBER_SUBSCRIPTIONS} invalid.  Must be int");
            }

            _providerName = providerName;
            _streamQueueMapper = new HashRingBasedStreamQueueMapper(_numSubscriptions, providerName);
            _adapterCache = new SimpleQueueAdapterCache(_cacheSize, logger);
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

            SerializationManager = serviceProvider.GetRequiredService<SerializationManager>();
            _adaptorFactory = () => ActivatorUtilities.GetServiceOrCreateInstance<TDataAdapter>(serviceProvider);
        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new PubSubAdapter<TDataAdapter>(_adaptorFactory(), SerializationManager, _logger, _streamQueueMapper, 
                _projectId, _topicId, _deploymentId, _providerName, _deadline, _customEndpoint);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => StreamFailureHandlerFactory(queueId);

        public IQueueAdapterCache GetQueueAdapterCache() => _adapterCache;

        public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;
    }
}
