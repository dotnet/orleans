using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Statistics;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Queue adapter factory which allows the PersistentStreamProvider to use EventHub as its backend persistent event queue.
    /// </summary>
    public class EventHubAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly IHostEnvironmentStatistics _hostEnvironmentStatistics;

        /// <summary>
        /// Data adapter
        /// </summary>
        protected readonly IEventHubDataAdapter dataAdapter;

        /// <summary>
        /// Orleans logging
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Framework service provider
        /// </summary>
        protected readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Stream provider settings
        /// </summary>
        private readonly EventHubOptions ehOptions;
        private readonly EventHubStreamCachePressureOptions cacheOptions;
        private readonly EventHubReceiverOptions receiverOptions;
        private readonly StreamStatisticOptions statisticOptions;
        private readonly StreamCacheEvictionOptions cacheEvictionOptions;
        private HashRingBasedPartitionedStreamQueueMapper streamQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, EventHubAdapterReceiver> receivers;
        private EventHubProducerClient client;

        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name { get; }

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
        protected Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, IEventHubQueueCache> CacheFactory { get; set; }

        /// <summary>
        /// Creates a partition checkpointer.
        /// </summary>
        private IStreamQueueCheckpointerFactory checkpointerFactory;

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }

        /// <summary>
        /// Create a queue mapper to map EventHub partitions to queues
        /// </summary>
        protected Func<string[], HashRingBasedPartitionedStreamQueueMapper> QueueMapperFactory { get; set; }

        /// <summary>
        /// Create a receiver monitor to report performance metrics.
        /// Factory function should return an IEventHubReceiverMonitor.
        /// </summary>
        protected Func<EventHubReceiverMonitorDimensions, ILoggerFactory, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory { get; set; }

        //for testing purpose, used in EventHubGeneratorStreamProvider
        /// <summary>
        /// Factory to create a IEventHubReceiver
        /// </summary>
        protected Func<EventHubPartitionSettings, string, ILogger, IEventHubReceiver> EventHubReceiverFactory;
        internal ConcurrentDictionary<QueueId, EventHubAdapterReceiver> EventHubReceivers => receivers;
        internal HashRingBasedPartitionedStreamQueueMapper EventHubQueueMapper => streamQueueMapper;

        public EventHubAdapterFactory(
            string name,
            EventHubOptions ehOptions,
            EventHubReceiverOptions receiverOptions,
            EventHubStreamCachePressureOptions cacheOptions, 
            StreamCacheEvictionOptions cacheEvictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdapter,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IHostEnvironmentStatistics hostEnvironmentStatistics)
        {
            this.Name = name;
            this.cacheEvictionOptions = cacheEvictionOptions ?? throw new ArgumentNullException(nameof(cacheEvictionOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this.ehOptions = ehOptions ?? throw new ArgumentNullException(nameof(ehOptions));
            this.cacheOptions = cacheOptions?? throw new ArgumentNullException(nameof(cacheOptions));
            this.dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            this.receiverOptions = receiverOptions?? throw new ArgumentNullException(nameof(receiverOptions));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _hostEnvironmentStatistics = hostEnvironmentStatistics;
        }

        public virtual void Init()
        {
            this.receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();

            InitEventHubClient();

            if (this.CacheFactory == null)
            {
                this.CacheFactory = CreateCacheFactory(this.cacheOptions).CreateCache;
            }

            if (this.StreamFailureHandlerFactory == null)
            {
                //TODO: Add a queue specific default failure handler with reasonable error reporting.
                this.StreamFailureHandlerFactory = partition => Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());
            }

            if (this.QueueMapperFactory == null)
            {
                this.QueueMapperFactory = partitions => new(partitions, this.Name);
            }

            if (this.ReceiverMonitorFactory == null)
            {
                this.ReceiverMonitorFactory = (dimensions, logger) => new DefaultEventHubReceiverMonitor(dimensions);
            }

            this.logger = this.loggerFactory.CreateLogger($"{this.GetType().FullName}.{this.ehOptions.EventHubName}");
        }

        //should only need checkpointer on silo side, so move its init logic when it is used
        private void InitCheckpointerFactory()
        {
            this.checkpointerFactory = this.serviceProvider.GetRequiredServiceByName<IStreamQueueCheckpointerFactory>(this.Name);
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
        /// Acquire delivery failure handler for a queue
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
        /// <param name="streamId"></param>
        /// <param name="events"></param>
        /// <param name="token"></param>
        /// <param name="requestContext"></param>
        /// <returns></returns>
        public virtual Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            EventData eventData = this.dataAdapter.ToQueueMessage(streamId, events, token, requestContext);
            string partitionKey = this.dataAdapter.GetPartitionKey(streamId);
            return this.client.SendAsync(new[] { eventData }, new SendEventOptions { PartitionKey = partitionKey });
        }

        /// <summary>
        /// Creates a queue receiver for the specified queueId
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
            return this.receivers.GetOrAdd(queueId, (q, instance) => instance.MakeReceiver(q), this);
        }

        protected virtual void InitEventHubClient()
        {
            var connectionOptions = ehOptions.ConnectionOptions;
            var connection = ehOptions.CreateConnection(connectionOptions);
            this.client = new EventHubProducerClient(connection, new EventHubProducerClientOptions { ConnectionOptions = connectionOptions });
        }

        /// <summary>
        /// Create a IEventHubQueueCacheFactory. It will create a EventHubQueueCacheFactory by default.
        /// User can override this function to return their own implementation of IEventHubQueueCacheFactory,
        /// and other customization of IEventHubQueueCacheFactory if they may.
        /// </summary>
        /// <returns></returns>
        protected virtual IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions eventHubCacheOptions)
        {
           var eventHubPath = this.ehOptions.EventHubName;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new EventHubQueueCacheFactory(eventHubCacheOptions, cacheEvictionOptions, statisticOptions, this.dataAdapter, sharedDimensions);
        }

        private EventHubAdapterReceiver MakeReceiver(QueueId queueId)
        {
            var config = new EventHubPartitionSettings
            {
                Hub = ehOptions,
                Partition = this.streamQueueMapper.QueueToPartition(queueId),
                ReceiverOptions = this.receiverOptions
            };

            var receiverMonitorDimensions = new EventHubReceiverMonitorDimensions
            {
                EventHubPartition = config.Partition,
                EventHubPath = config.Hub.EventHubName,
            };
            if (this.checkpointerFactory == null)
                InitCheckpointerFactory();
            return new EventHubAdapterReceiver(
                config,
                this.CacheFactory,
                this.checkpointerFactory.Create,
                this.loggerFactory,
                this.ReceiverMonitorFactory(receiverMonitorDimensions, this.loggerFactory),
                this.serviceProvider.GetRequiredService<IOptions<LoadSheddingOptions>>().Value,
                _hostEnvironmentStatistics,
                this.EventHubReceiverFactory);
        }

        /// <summary>
        /// Get partition Ids from eventhub
        /// </summary>
        /// <returns></returns>
        protected virtual async Task<string[]> GetPartitionIdsAsync()
        {
            return await client.GetPartitionIdsAsync();
        }

        public static EventHubAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetServiceByName<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<EventHubAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
