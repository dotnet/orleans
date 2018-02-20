using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Configuration;

namespace Orleans.Providers.GCP.Streams.PubSub
{
    public class PubSubAdapterFactory<TDataAdapter> : IQueueAdapterFactory
        where TDataAdapter : IPubSubDataAdapter
    {
        private readonly string _providerName;
        private readonly PubSubStreamOptions options;
        private readonly SiloOptions siloOptions;
        private readonly ILoggerFactory loggerFactory;
        private readonly Func<TDataAdapter> _adaptorFactory;
        private HashRingBasedStreamQueueMapper _streamQueueMapper;
        private IQueueAdapterCache _adapterCache;

        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Application level failure handler override.
        /// </summary>
        protected Func<QueueId, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { private get; set; }

        public PubSubAdapterFactory(string name, PubSubStreamOptions options, IServiceProvider serviceProvider, IOptions<SiloOptions> siloOptions, SerializationManager serializationManager, ILoggerFactory loggerFactory)
        {
            this._providerName = name;
            this.options = options;
            this.siloOptions = siloOptions.Value;
            this.SerializationManager = serializationManager;
            this.loggerFactory = loggerFactory;
            this._adaptorFactory = () => ActivatorUtilities.GetServiceOrCreateInstance<TDataAdapter>(serviceProvider);
        }

        public virtual void Init()
        {
            this._streamQueueMapper = new HashRingBasedStreamQueueMapper(this.options.NumSubscriptions, this._providerName);
            this._adapterCache = new SimpleQueueAdapterCache(this.options.CacheSize, this._providerName, loggerFactory);
            if (StreamFailureHandlerFactory == null)
            {
                StreamFailureHandlerFactory =
                    qid => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new PubSubAdapter<TDataAdapter>(_adaptorFactory(), SerializationManager, this.loggerFactory, _streamQueueMapper,
                this.options.ProjectId, this.options.TopicId, this.options.ClusterId ?? this.siloOptions.ClusterId, this._providerName, this.options.Deadline, this.options.CustomEndpoint);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => StreamFailureHandlerFactory(queueId);

        public IQueueAdapterCache GetQueueAdapterCache() => _adapterCache;

        public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

        public static PubSubAdapterFactory<TDataAdapter> Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<PubSubStreamOptions> streamOptionsSnapshot = services.GetRequiredService<IOptionsSnapshot<PubSubStreamOptions>>();
            var factory = ActivatorUtilities.CreateInstance<PubSubAdapterFactory<TDataAdapter>>(services, name, streamOptionsSnapshot.Get(name));
            factory.Init();
            return factory;
        }
    }
}
