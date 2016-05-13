
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
        protected Logger logger;
        protected IServiceProvider serviceProvider;
        protected EventHubStreamProviderConfig adapterConfig;
        protected IEventHubSettings hubSettings;
        protected ICheckpointerSettings checkpointerSettings;
        private EventHubQueueMapper streamQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, EventHubAdapterReceiver> receivers;
        private EventHubClient client;

        public string Name => adapterConfig.StreamProviderName;
        public bool IsRewindable => true;
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        protected Func<string, IStreamQueueCheckpointer<string>, IEventHubQueueCache> CacheFactory { get; set; }
        protected Func<string, Task<IStreamQueueCheckpointer<string>>> CheckpointerFactory { get; set; }
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }
        
        /// <summary>
        /// Factory initialization.
        /// Provider config must contain the event hub settings type or the settings themselves.
        /// EventHubSettingsType is recommended for consumers that do not want to include secure information in the cluster configuration.
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="providerName"></param>
        /// <param name="log"></param>
        /// <param name="svcProvider"></param>
        public void Init(IProviderConfiguration providerConfig, string providerName, Logger log, IServiceProvider svcProvider)
        {
            if (providerConfig == null) throw new ArgumentNullException("providerConfig");
            if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentNullException("providerName");
            if (log == null) throw new ArgumentNullException("log");
            if (svcProvider == null) throw new ArgumentNullException("svcProvider");

            logger = log;
            serviceProvider = svcProvider;
            receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();

            adapterConfig = new EventHubStreamProviderConfig(providerName);
            adapterConfig.PopulateFromProviderConfig(providerConfig);
            hubSettings = adapterConfig.GetEventHubSettings(providerConfig, serviceProvider);
            client = EventHubClient.CreateFromConnectionString(hubSettings.ConnectionString, hubSettings.Path);

            if (CheckpointerFactory == null)
            {
                checkpointerSettings = adapterConfig.GetCheckpointerSettings(providerConfig, serviceProvider);
                CheckpointerFactory = partition => EventHubCheckpointer.Create(checkpointerSettings, adapterConfig.StreamProviderName, partition);
            }
            
            if (CacheFactory == null)
            {
                var bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterConfig.CacheSizeMb, pool => new FixedSizeBuffer(1 << 20, pool));
                CacheFactory = (partition,checkpointer) => new EventHubQueueCache(checkpointer, bufferPool);
            }

            if (StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }
        }

        public async Task<IQueueAdapter> CreateAdapter()
        {
            if (streamQueueMapper == null)
            {
                partitionIds = await GetPartitionIdsAsync();
                streamQueueMapper = new EventHubQueueMapper(partitionIds, adapterConfig.StreamProviderName);
            }
            return this;
        }

        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            //TODO: CreateAdapter must be called first.  Figure out how to safely enforce this
            return streamQueueMapper;
        }

        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return StreamFailureHandlerFactory(streamQueueMapper.QueueToPartition(queueId));
        }

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

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

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
            var config = new EventHubPartitionConfig
            {
                Hub = hubSettings,
                Partition = streamQueueMapper.QueueToPartition(queueId),
            };
            return new EventHubAdapterReceiver(config, CacheFactory, CheckpointerFactory, logger);
        }

        private async Task<string[]> GetPartitionIdsAsync()
        {
            NamespaceManager namespaceManager = NamespaceManager.CreateFromConnectionString(hubSettings.ConnectionString);
            EventHubDescription hubDescription = await namespaceManager.GetEventHubAsync(hubSettings.Path);
            return hubDescription.PartitionIds;
        }
    }
}
