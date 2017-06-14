
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Runtime.Configuration;
using OrleansServiceBus.Providers.Streams.EventHub.StatisticMonitors;

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
        protected Logger logger;

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
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name => adapterSettings.StreamProviderName;

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
        protected Func<string, IStreamQueueCheckpointer<string>, Logger, IEventHubQueueCache> CacheFactory { get; set; }

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
        protected Func<EventHubReceiverMonitorDimensions, Logger, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory { get; set; }


        //for testing purpose, used in EventHubGeneratorStreamProvider
        /// <summary>
        /// Factory to create a IEventHubReceiver
        /// </summary>
        protected Func<EventHubPartitionSettings, string, Logger, Task<IEventHubReceiver>> EventHubReceiverFactory;
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
        public virtual void Init(IProviderConfiguration providerCfg, string providerName, Logger log, IServiceProvider svcProvider)
        {
            if (providerCfg == null) throw new ArgumentNullException(nameof(providerCfg));
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException(nameof(providerName));
            if (log == null) throw new ArgumentNullException(nameof(log));

            providerConfig = providerCfg;
            serviceProvider = svcProvider;
            receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();
            this.SerializationManager = this.serviceProvider.GetRequiredService<SerializationManager>();
            adapterSettings = new EventHubStreamProviderSettings(providerName);
            adapterSettings.PopulateFromProviderConfig(providerConfig);
            hubSettings = adapterSettings.GetEventHubSettings(providerConfig, serviceProvider);
            InitEventHubClient();
            if (CheckpointerFactory == null)
            {
                checkpointerSettings = adapterSettings.GetCheckpointerSettings(providerConfig, serviceProvider);
                CheckpointerFactory = partition => EventHubCheckpointer.Create(checkpointerSettings, adapterSettings.StreamProviderName, partition);
            }

            if (CacheFactory == null)
            {
                CacheFactory = CreateCacheFactory(adapterSettings).CreateCache;
            }

            if (StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

            if (QueueMapperFactory == null)
            {
                QueueMapperFactory = partitions => new EventHubQueueMapper(partitions, adapterSettings.StreamProviderName);
            }

            if (ReceiverMonitorFactory == null)
            {
                ReceiverMonitorFactory = (dimensions, receiverLogger) => new DefaultEventHubReceiverMonitor(dimensions, receiverLogger.GetSubLogger(typeof(DefaultEventHubReceiverMonitor).Name));
            }

            logger = log.GetLogger($"EventHub.{hubSettings.Path}");
        }

        /// <summary>
        /// Create queue adapter.
        /// </summary>
        /// <returns></returns>
        public async Task<IQueueAdapter> CreateAdapter()
        {
            if (streamQueueMapper == null)
            {
                partitionIds = await GetPartitionIdsAsync();
                streamQueueMapper = QueueMapperFactory(partitionIds);
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
            return streamQueueMapper;
        }

        /// <summary>
        /// Aquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return StreamFailureHandlerFactory(streamQueueMapper.QueueToPartition(queueId));
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
#if NETSTANDARD
            return client.SendAsync(eventData, streamGuid.ToString());
#else
            return client.SendAsync(eventData);
#endif
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
            return receivers.GetOrAdd(queueId, q => MakeReceiver(queueId));
        }

        protected virtual void InitEventHubClient()
        {
#if NETSTANDARD
            var connectionStringBuilder = new EventHubsConnectionStringBuilder(hubSettings.ConnectionString)
            {
                EntityPath = hubSettings.Path
            };
            client = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());
#else
            client = EventHubClient.CreateFromConnectionString(hubSettings.ConnectionString, hubSettings.Path);
#endif
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
            var globalConfig = this.serviceProvider.GetService<GlobalConfiguration>();
            var nodeConfig = this.serviceProvider.GetService<NodeConfiguration>();
            var eventHubPath = hubSettings.Path;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(globalConfig, nodeConfig, eventHubPath);
            return new EventHubQueueCacheFactory(providerSettings, SerializationManager, sharedDimensions);
        }
 
        private EventHubAdapterReceiver MakeReceiver(QueueId queueId)
        {
            var config = new EventHubPartitionSettings
            {
                Hub = hubSettings,
                Partition = streamQueueMapper.QueueToPartition(queueId),
            };
            Logger recieverLogger = logger.GetSubLogger($"{config.Partition}");
            
            var receiverMonitorDimensions = new EventHubReceiverMonitorDimensions();
            receiverMonitorDimensions.EventHubPartition = config.Partition;
            receiverMonitorDimensions.EventHubPath = config.Hub.Path;
            receiverMonitorDimensions.NodeConfig = this.serviceProvider.GetRequiredService<NodeConfiguration>();
            receiverMonitorDimensions.GlobalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();

            return new EventHubAdapterReceiver(config, CacheFactory, CheckpointerFactory, recieverLogger, ReceiverMonitorFactory(receiverMonitorDimensions, recieverLogger), 
                this.serviceProvider.GetRequiredService<Func<NodeConfiguration>>(),
                this.EventHubReceiverFactory);
        }

        /// <summary>
        /// Get partition Ids from eventhub
        /// </summary>
        /// <returns></returns>
        protected virtual async Task<string[]> GetPartitionIdsAsync()
        {
#if NETSTANDARD
            EventHubRuntimeInformation runtimeInfo = await client.GetRuntimeInformationAsync();
            return runtimeInfo.PartitionIds;
#else
            NamespaceManager namespaceManager = NamespaceManager.CreateFromConnectionString(hubSettings.ConnectionString);
            EventHubDescription hubDescription = await namespaceManager.GetEventHubAsync(hubSettings.Path);
            return hubDescription.PartitionIds;
#endif
        }
    }
}