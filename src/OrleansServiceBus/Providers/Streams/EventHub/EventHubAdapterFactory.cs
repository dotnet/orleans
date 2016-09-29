
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

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
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

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

            adapterSettings = new EventHubStreamProviderSettings(providerName);
            adapterSettings.PopulateFromProviderConfig(providerConfig);
            hubSettings = adapterSettings.GetEventHubSettings(providerConfig, serviceProvider);
            client = EventHubClient.CreateFromConnectionString(hubSettings.ConnectionString, hubSettings.Path);

            if (CheckpointerFactory == null)
            {
                checkpointerSettings = adapterSettings.GetCheckpointerSettings(providerConfig, serviceProvider);
                CheckpointerFactory = partition => EventHubCheckpointer.Create(checkpointerSettings, adapterSettings.StreamProviderName, partition);
            }

            if (CacheFactory == null)
            {
                var bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterSettings.CacheSizeMb, () => new FixedSizeBuffer(1 << 20));
                var timePurge = new TimePurgePredicate(adapterSettings.DataMinTimeInCache, adapterSettings.DataMaxAgeInCache);
                CacheFactory = (partition,checkpointer,cacheLogger) => new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger);
            }

            if (StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

            if (QueueMapperFactory == null)
            {
                QueueMapperFactory = partitions => new EventHubQueueMapper(partitionIds, adapterSettings.StreamProviderName);
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
        public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            if (token != null)
            {
                throw new NotImplementedException("EventHub stream provider currently does not support non-null StreamSequenceToken.");
            }
            EventData eventData = EventHubBatchContainer.ToEventData(streamGuid, streamNamespace, events, requestContext);
            return client.SendAsync(eventData);
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

        private EventHubAdapterReceiver MakeReceiver(QueueId queueId)
        {
            var config = new EventHubPartitionSettings
            {
                Hub = hubSettings,
                Partition = streamQueueMapper.QueueToPartition(queueId),
            };
            Logger recieverLogger = logger.GetSubLogger($"{config.Partition}");
            return new EventHubAdapterReceiver(config, CacheFactory, CheckpointerFactory, recieverLogger);
        }

        private async Task<string[]> GetPartitionIdsAsync()
        {
            NamespaceManager namespaceManager = NamespaceManager.CreateFromConnectionString(hubSettings.ConnectionString);
            EventHubDescription hubDescription = await namespaceManager.GetEventHubAsync(hubSettings.Path);
            return hubDescription.PartitionIds;
        }
    }
}
