using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Threading.Tasks;

namespace Orleans.Serialization.Providers.Streams
{
    public class GooglePubSubAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IGooglePubSubDataAdapter
    {
        private string _projectId;
        private string _topicId;
        private string _deploymentId;
        private string _providerName;
        private int _cacheSize;
        private int _numSubscriptions;
        private TimeSpan? _deadline;
        private HashRingBasedStreamQueueMapper _streamQueueMapper;
        private IQueueAdapterCache _adapterCache;
        private Func<TDataAdapter> _adaptorFactory;

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
            if (!config.Properties.TryGetValue(GooglePubSubAdapterConstants.PROJECT_ID, out _projectId))
                throw new ArgumentException($"{GooglePubSubAdapterConstants.PROJECT_ID} property not set");
            if (!config.Properties.TryGetValue(GooglePubSubAdapterConstants.TOPIC_ID, out _topicId))
                throw new ArgumentException($"{GooglePubSubAdapterConstants.TOPIC_ID} property not set");
            if (!config.Properties.TryGetValue(GooglePubSubAdapterConstants.DEPLOYMENT_ID, out _deploymentId))
                throw new ArgumentException($"{GooglePubSubAdapterConstants.DEPLOYMENT_ID} property not set");

            string deadlineStr;
            if (config.Properties.TryGetValue(GooglePubSubAdapterConstants.DEADLINE, out deadlineStr))
            {
                TimeSpan deadline;
                if (!TimeSpan.TryParse(deadlineStr, out deadline))
                {
                    throw new ArgumentException(
                        $"Failed to parse {GooglePubSubAdapterConstants.DEADLINE} value '{deadlineStr}' as a TimeSpan");
                }

                _deadline = deadline;
            }
            else
            {
                _deadline = null;
            }

            _cacheSize = SimpleQueueAdapterCache.ParseSize(config, GooglePubSubAdapterConstants.CACHE_SIZE_DEFAULT);

            string numSubscriptionsString;
            _numSubscriptions = GooglePubSubAdapterConstants.NUMBER_SUBSCRIPTIONS_DEFAULT;
            if (config.Properties.TryGetValue(GooglePubSubAdapterConstants.NUMBER_SUBSCRIPTIONS, out numSubscriptionsString))
            {
                if (!int.TryParse(numSubscriptionsString, out _numSubscriptions))
                    throw new ArgumentException($"{GooglePubSubAdapterConstants.NUMBER_SUBSCRIPTIONS} invalid.  Must be int");
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
            var adapter = new GooglePubSubAdapter<TDataAdapter>(_adaptorFactory(), SerializationManager, _streamQueueMapper, _projectId, _topicId, _deploymentId, _providerName, _deadline);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => StreamFailureHandlerFactory(queueId);

        public IQueueAdapterCache GetQueueAdapterCache() => _adapterCache;

        public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;
    }
}
