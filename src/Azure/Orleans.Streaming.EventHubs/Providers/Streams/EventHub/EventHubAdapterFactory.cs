using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.EventHubs;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Queue adapter factory which allows the PersistentStreamProvider to use EventHub as its backend persistent event queue.
    /// </summary>
    public class EventHubAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        /// <summary>
        /// Orleans logging
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Framework service provider
        /// </summary>
        protected IServiceProvider serviceProvider;

        /// <summary>
        /// Provider configuration
        /// </summary>
        protected IProviderConfiguration providerConfig;

        /// <summary>
        /// Stream provider settings
        /// </summary>
        protected EventHubStreamProviderSettings adapterSettings;

        /// <summary>
        /// Event Hub settings
        /// </summary>
        protected IEventHubSettings hubSettings;

        /// <summary>
        /// Checkpointer settings
        /// </summary>
        protected ICheckpointerSettings checkpointerSettings;

        private IEventHubQueueMapper streamQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, EventHubAdapterReceiver> receivers;
        private EventHubClient client;
        private ITelemetryProducer telemetryProducer;
		private ILoggerFactory loggerFactory;        
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name => this.adapterSettings.StreamProviderName;

        /// <summary>
        /// Determines whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        public bool IsRewindable => true;

        /// <summary>
        /// Direction of this queue adapter: Read, Write or ReadWrite.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        public StreamProviderDirection Direction { get; protected set; } = StreamProviderDirection.ReadWrite;

        /// <summary>
        /// Creates a message cache for an eventhub partition.
        /// </summary>
        protected Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, ITelemetryProducer, IEventHubQueueCache> CacheFactory { get; set; }

        /// <summary>
        /// Creates a parition checkpointer.
        /// </summary>
        protected Func<string, Task<IStreamQueueCheckpointer<string>>> CheckpointerFactory { get; set; }

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }

        /// <summary>
        /// Create a queue mapper to map EventHub partitions to queues
        /// </summary>
        protected Func<string[], IEventHubQueueMapper> QueueMapperFactory { get; set; }

        /// <summary>
        /// Create a receiver monitor to report performance metrics.
        /// Factory funciton should return an IEventHubReceiverMonitor.
        /// </summary>
        protected Func<EventHubReceiverMonitorDimensions, ILoggerFactory, ITelemetryProducer, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory { get; set; }

        //for testing purpose, used in EventHubGeneratorStreamProvider
        /// <summary>
        /// Factory to create a IEventHubReceiver
        /// </summary>
        protected Func<EventHubPartitionSettings, string, ILogger, ITelemetryProducer, Task<IEventHubReceiver>> EventHubReceiverFactory;
        internal ConcurrentDictionary<QueueId, EventHubAdapterReceiver> EventHubReceivers { get { return this.receivers; } }
        internal IEventHubQueueMapper EventHubQueueMapper { get { return this.streamQueueMapper; } }
        /// <summary>
        /// Factory initialization.
        /// Provider config must contain the event hub settings type or the settings themselves.
        /// EventHubSettingsType is recommended for consumers that do not want to include secure information in the cluster configuration.
        /// </summary>
        /// <param name="providerCfg"></param>
        /// <param name="providerName"></param>
        /// <param name="log"></param>
        /// <param name="svcProvider"></param>
        public virtual void Init(IProviderConfiguration providerCfg, string providerName, IServiceProvider svcProvider)
        {
            if (providerCfg == null) throw new ArgumentNullException(nameof(providerCfg));
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));

            this.providerConfig = providerCfg;
            this.serviceProvider = svcProvider;
            this.loggerFactory = this.serviceProvider.GetRequiredService<ILoggerFactory>();
            this.receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();
            this.SerializationManager = this.serviceProvider.GetRequiredService<SerializationManager>();
            this.adapterSettings = new EventHubStreamProviderSettings(providerName);
            this.adapterSettings.PopulateFromProviderConfig(this.providerConfig);
            this.hubSettings = this.adapterSettings.GetEventHubSettings(this.providerConfig, this.serviceProvider);
            this.telemetryProducer = this.serviceProvider.GetService<ITelemetryProducer>();

            InitEventHubClient();
            if (this.CheckpointerFactory == null)
            {
                this.checkpointerSettings = this.adapterSettings.GetCheckpointerSettings(this.providerConfig, this.serviceProvider);
                this.CheckpointerFactory = partition => EventHubCheckpointer.Create(this.checkpointerSettings, this.adapterSettings.StreamProviderName, partition, this.loggerFactory);
            }

            if (this.CacheFactory == null)
            {
                this.CacheFactory = CreateCacheFactory(this.adapterSettings).CreateCache;
            }

            if (this.StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                this.StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

            if (this.QueueMapperFactory == null)
            {
                this.QueueMapperFactory = partitions => new EventHubQueueMapper(partitions, this.adapterSettings.StreamProviderName);
            }

            if (this.ReceiverMonitorFactory == null)
            {
                this.ReceiverMonitorFactory = (dimensions, logger, telemetryProducer) => new DefaultEventHubReceiverMonitor(dimensions, telemetryProducer);
            }

            this.logger = this.loggerFactory.CreateLogger($"{this.GetType().FullName}.{this.hubSettings.Path}");
        }

        /// <summary>
        /// Create queue adapter.
        /// </summary>
        /// <returns></returns>
        public async Task<IQueueAdapter> CreateAdapter()
        {
            if (this.streamQueueMapper == null)
            {
                this.partitionIds = await GetPartitionIdsAsync();
                this.streamQueueMapper = this.QueueMapperFactory(this.partitionIds);
            }
            return this;
        }

        /// <summary>
        /// Create queue message cache adapter
        /// </summary>
        /// <returns></returns>
        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        /// <summary>
        /// Create queue mapper
        /// </summary>
        /// <returns></returns>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            //TODO: CreateAdapter must be called first.  Figure out how to safely enforce this
            return this.streamQueueMapper;
        }

        /// <summary>
        /// Aquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return this.StreamFailureHandlerFactory(this.streamQueueMapper.QueueToPartition(queueId));
        }

        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streamGuid"></param>
        /// <param name="streamNamespace"></param>
        /// <param name="events"></param>
        /// <param name="token"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        public virtual Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new NotImplementedException("EventHub stream provider currently does not support non-null StreamSequenceToken.");
            }
            EventData eventData = EventHubBatchContainer.ToEventData(this.SerializationManager, streamGuid, streamNamespace, events, requestContext);

            return this.client.SendAsync(eventData, streamGuid.ToString());
        }

        /// <summary>
        /// Creates a quere receiver for the specificed queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId"></param>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        private EventHubAdapterReceiver GetOrCreateReceiver(QueueId queueId)
        {
            return this.receivers.GetOrAdd(queueId, q => MakeReceiver(queueId));
        }

        protected virtual void InitEventHubClient()
        {
            var connectionStringBuilder = new EventHubsConnectionStringBuilder(this.hubSettings.ConnectionString)
            {
                EntityPath = this.hubSettings.Path
            };
            this.client = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());
        }

        /// <summary>
        /// Create a IEventHubQueueCacheFactory. It will create a EventHubQueueCacheFactory by default.
        /// User can override this function to return their own implementation of IEventHubQueueCacheFactory,
        /// and other customization of IEventHubQueueCacheFactory if they may.
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <returns></returns>
        protected virtual IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
        {
            var eventHubPath = this.hubSettings.Path;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new EventHubQueueCacheFactory(providerSettings, this.SerializationManager, sharedDimensions, this.loggerFactory);
        }

        private EventHubAdapterReceiver MakeReceiver(QueueId queueId)
        {
            var config = new EventHubPartitionSettings
            {
                Hub = hubSettings,
                Partition = this.streamQueueMapper.QueueToPartition(queueId),
            };

            var receiverMonitorDimensions = new EventHubReceiverMonitorDimensions
            {
                EventHubPartition = config.Partition,
                EventHubPath = config.Hub.Path,
            };

            return new EventHubAdapterReceiver(config, this.CacheFactory, this.CheckpointerFactory, this.loggerFactory, this.ReceiverMonitorFactory(receiverMonitorDimensions, this.loggerFactory, this.telemetryProducer), 
                this.serviceProvider.GetRequiredService<IOptions<LoadSheddingOptions>>().Value,
                this.telemetryProducer,
                this.EventHubReceiverFactory);
        }

        /// <summary>
        /// Get partition Ids from eventhub
        /// </summary>
        /// <returns></returns>
        protected virtual async Task<string[]> GetPartitionIdsAsync()
        {
            EventHubRuntimeInformation runtimeInfo = await client.GetRuntimeInformationAsync();
            return runtimeInfo.PartitionIds;
        }
    }
}