using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Queue adapter factory which allows the PersistentStreamProvider to use EventHub as its backend persistent event queue.
    /// </summary>
    public class EventHubAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// Orleans logging
        /// </summary>
        protected ILogger logger;

        /// <summary>
        /// Framework service provider
        /// </summary>
        protected IServiceProvider serviceProvider;

        /// <summary>
        /// Stream provider settings
        /// </summary>
        private EventHubOptions ehOptions;
        private EventHubStreamCachePressureOptions cacheOptions;
        private EventHubReceiverOptions receiverOptions;
        private StreamStatisticOptions statisticOptions;
        private StreamCacheEvictionOptions cacheEvictionOptions;
        private IEventHubQueueMapper streamQueueMapper;
        private string[] partitionIds;
        private ConcurrentDictionary<QueueId, EventHubAdapterReceiver> receivers;
        private EventHubClient client;
        private ITelemetryProducer telemetryProducer;
        /// <summary>
        /// Gets the serialization manager.
        /// </summary>
        public SerializationManager SerializationManager { get; private set; }

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
        protected Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, ITelemetryProducer, IEventHubQueueCache> CacheFactory { get; set; }

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
        protected Func<string[], IEventHubQueueMapper> QueueMapperFactory { get; set; }

        /// <summary>
        /// Create a receiver monitor to report performance metrics.
        /// Factory function should return an IEventHubReceiverMonitor.
        /// </summary>
        protected Func<EventHubReceiverMonitorDimensions, ILoggerFactory, ITelemetryProducer, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory { get; set; }

        //for testing purpose, used in EventHubGeneratorStreamProvider
        /// <summary>
        /// Factory to create a IEventHubReceiver
        /// </summary>
        protected Func<EventHubPartitionSettings, string, ILogger, ITelemetryProducer, Task<IEventHubReceiver>> EventHubReceiverFactory;
        internal ConcurrentDictionary<QueueId, EventHubAdapterReceiver> EventHubReceivers { get { return this.receivers; } }
        internal IEventHubQueueMapper EventHubQueueMapper { get { return this.streamQueueMapper; } }

        public EventHubAdapterFactory(string name, EventHubOptions ehOptions, EventHubReceiverOptions receiverOptions, EventHubStreamCachePressureOptions cacheOptions, 
            StreamCacheEvictionOptions cacheEvictionOptions, StreamStatisticOptions statisticOptions,
            IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
        {
            this.Name = name;
            this.cacheEvictionOptions = cacheEvictionOptions ?? throw new ArgumentNullException(nameof(cacheEvictionOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this.ehOptions = ehOptions ?? throw new ArgumentNullException(nameof(ehOptions));
            this.cacheOptions = cacheOptions?? throw new ArgumentNullException(nameof(cacheOptions));
            this.receiverOptions = receiverOptions?? throw new ArgumentNullException(nameof(receiverOptions));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.SerializationManager = serializationManager ?? throw new ArgumentNullException(nameof(serializationManager));
            this.telemetryProducer = telemetryProducer ?? throw new ArgumentNullException(nameof(telemetryProducer));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        public virtual void Init()
        {
            this.receivers = new ConcurrentDictionary<QueueId, EventHubAdapterReceiver>();
            this.telemetryProducer = this.serviceProvider.GetService<ITelemetryProducer>();

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
                this.QueueMapperFactory = partitions => new EventHubQueueMapper(partitions, this.Name);
            }

            if (this.ReceiverMonitorFactory == null)
            {
                this.ReceiverMonitorFactory = (dimensions, logger, telemetryProducer) => new DefaultEventHubReceiverMonitor(dimensions, telemetryProducer);
            }

            this.logger = this.loggerFactory.CreateLogger($"{this.GetType().FullName}.{this.ehOptions.Path}");
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
            return this.receivers.GetOrAdd(queueId, q => MakeReceiver(queueId));
        }

        protected virtual void InitEventHubClient()
        {
            var connectionStringBuilder = new EventHubsConnectionStringBuilder(this.ehOptions.ConnectionString)
            {
                EntityPath = this.ehOptions.Path
            };
            this.client = EventHubClient.CreateFromConnectionString(connectionStringBuilder.ToString());
        }

        /// <summary>
        /// Create a IEventHubQueueCacheFactory. It will create a EventHubQueueCacheFactory by default.
        /// User can override this function to return their own implementation of IEventHubQueueCacheFactory,
        /// and other customization of IEventHubQueueCacheFactory if they may.
        /// </summary>
        /// <returns></returns>
        protected virtual IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions eventHubCacheOptions)
        {
            var eventHubPath = this.ehOptions.Path;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new EventHubQueueCacheFactory(eventHubCacheOptions, cacheEvictionOptions, statisticOptions, this.SerializationManager, sharedDimensions);
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
                EventHubPath = config.Hub.Path,
            };
            if (this.checkpointerFactory == null)
                InitCheckpointerFactory();
            return new EventHubAdapterReceiver(config, this.CacheFactory, this.checkpointerFactory.Create, this.loggerFactory, this.ReceiverMonitorFactory(receiverMonitorDimensions, this.loggerFactory, this.telemetryProducer), 
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

        public static EventHubAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            var factory = ActivatorUtilities.CreateInstance<EventHubAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions);
            factory.Init();
            return factory;
        }
    }
}