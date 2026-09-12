using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Queue adapter factory which allows the PersistentStreamProvider to use EventHub as its backend persistent event queue.
    /// </summary>
    public class EventHubAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly IEnvironmentStatisticsProvider environmentStatisticsProvider;
        private readonly OrleansInstruments orleansInstruments;

        /// <summary>
        /// Data adapter
        /// </summary>
        protected readonly IEventHubDataAdapter dataAdapter;

        /// <summary>
        /// Orleans logging
        /// </summary>
        protected ILogger logger = null!;

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
        private HashRingBasedPartitionedStreamQueueMapper streamQueueMapper = null!;
        private string[] partitionIds = null!;
        private QueueAdapterReceiverRegistry<EventHubAdapterReceiver> receivers = null!;
        private EventHubProducerClient client = null!;

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
        protected Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, IEventHubQueueCache> CacheFactory { get; set; } = null!;

        /// <summary>
        /// Creates a partition checkpointer.
        /// </summary>
        private IStreamQueueCheckpointerFactory checkpointerFactory = null!;

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; } = null!;

        /// <summary>
        /// Create a queue mapper to map EventHub partitions to queues
        /// </summary>
        protected Func<string[], HashRingBasedPartitionedStreamQueueMapper> QueueMapperFactory { get; set; } = null!;

        /// <summary>
        /// Create a receiver monitor to report performance metrics.
        /// Factory function should return an IEventHubReceiverMonitor.
        /// </summary>
        protected Func<EventHubReceiverMonitorDimensions, ILoggerFactory, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory { get; set; } = null!;

        //for testing purpose, used in EventHubGeneratorStreamProvider
        /// <summary>
        /// Factory to create a IEventHubReceiver
        /// </summary>
        protected Func<EventHubPartitionSettings, string, ILogger, IEventHubReceiver> EventHubReceiverFactory = null!;
        internal IReadOnlyDictionary<QueueId, EventHubAdapterReceiver> EventHubReceivers => receivers.Receivers;
        internal HashRingBasedPartitionedStreamQueueMapper EventHubQueueMapper => streamQueueMapper;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubAdapterFactory"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="ehOptions">The Event Hub connection options.</param>
        /// <param name="receiverOptions">The Event Hub receiver options.</param>
        /// <param name="cacheOptions">The Event Hub cache pressure options.</param>
        /// <param name="cacheEvictionOptions">The stream cache eviction options.</param>
        /// <param name="statisticOptions">The stream statistics options.</param>
        /// <param name="dataAdapter">The adapter used to convert between Event Hubs data and Orleans stream data.</param>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="environmentStatisticsProvider">The environment statistics provider.</param>
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
            IEnvironmentStatisticsProvider environmentStatisticsProvider)
        {
            this.Name = name;
            this.cacheEvictionOptions = cacheEvictionOptions ?? throw new ArgumentNullException(nameof(cacheEvictionOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentNullException(nameof(statisticOptions));
            this.ehOptions = ehOptions ?? throw new ArgumentNullException(nameof(ehOptions));
            this.cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
            this.dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            this.receiverOptions = receiverOptions ?? throw new ArgumentNullException(nameof(receiverOptions));
            this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.environmentStatisticsProvider = environmentStatisticsProvider;
            this.orleansInstruments = serviceProvider.GetRequiredService<OrleansInstruments>();
        }

        /// <summary>
        /// Initializes the adapter factory and its Event Hub client, cache, queue mapping, and monitoring components.
        /// </summary>
        public virtual void Init()
        {
            this.receivers = new QueueAdapterReceiverRegistry<EventHubAdapterReceiver>(MakeReceiver);

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
                this.ReceiverMonitorFactory = (dimensions, logger) => new DefaultEventHubReceiverMonitor(dimensions, this.orleansInstruments);
            }

            this.logger = this.loggerFactory.CreateLogger($"{this.GetType().FullName}.{this.ehOptions.EventHubName}");
        }

        //should only need checkpointer on silo side, so move its init logic when it is used
        [MemberNotNull(nameof(checkpointerFactory))]
        private void InitCheckpointerFactory()
        {
            this.checkpointerFactory = this.serviceProvider.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(this.Name);
        }
        /// <summary>
        /// Creates the queue adapter and initializes its partition mapping.
        /// </summary>
        /// <returns>The queue adapter.</returns>
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
        /// Gets the queue cache factory implemented by this instance.
        /// </summary>
        /// <returns>The queue cache factory.</returns>
        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        /// <summary>
        /// Gets the mapper between Orleans stream queues and Event Hub partitions.
        /// </summary>
        /// <returns>The stream queue mapper.</returns>
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            //TODO: CreateAdapter must be called first.  Figure out how to safely enforce this
            return this.streamQueueMapper;
        }

        /// <summary>
        /// Gets the delivery failure handler for a queue.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>A task which resolves to the delivery failure handler.</returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return this.StreamFailureHandlerFactory(this.streamQueueMapper.QueueToPartition(queueId));
        }

        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T">The event type.</typeparam>
        /// <param name="streamId">The destination stream.</param>
        /// <param name="events">The events to enqueue.</param>
        /// <param name="token">The stream sequence token, which must be <see langword="null"/> for Event Hubs streams.</param>
        /// <param name="requestContext">The request context to propagate with the events.</param>
        /// <returns>A task which represents the enqueue operation.</returns>
        public virtual Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token,
            Dictionary<string, object>? requestContext)
        {
            EventData eventData = this.dataAdapter.ToQueueMessage(streamId, events, token, requestContext);
            string partitionKey = this.dataAdapter.GetPartitionKey(streamId);
            return this.client.SendAsync(new[] { eventData }, new SendEventOptions { PartitionKey = partitionKey });
        }

        /// <summary>
        /// Creates a queue receiver for the specified queue.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>The queue receiver.</returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        /// <summary>
        /// Creates a cache for the specified queue.
        /// </summary>
        /// <param name="queueId">The queue identifier.</param>
        /// <returns>The queue cache.</returns>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            return GetOrCreateReceiver(queueId);
        }

        private EventHubAdapterReceiver GetOrCreateReceiver(QueueId queueId)
        {
            return this.receivers.GetOrCreate(queueId);
        }

        /// <summary>
        /// Initializes the Event Hub producer client.
        /// </summary>
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
        /// <param name="eventHubCacheOptions">The cache pressure options.</param>
        /// <returns>The Event Hub queue cache factory.</returns>
        protected virtual IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions eventHubCacheOptions)
        {
            var eventHubPath = this.ehOptions.EventHubName;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new EventHubQueueCacheFactory(eventHubCacheOptions, cacheEvictionOptions, statisticOptions, this.dataAdapter, sharedDimensions, this.orleansInstruments);
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
                (partition, cancellationToken) => this.checkpointerFactory.Create(partition, cancellationToken),
                this.loggerFactory,
                this.ReceiverMonitorFactory(receiverMonitorDimensions, this.loggerFactory),
                this.serviceProvider.GetRequiredService<IOptions<LoadSheddingOptions>>().Value,
                this.environmentStatisticsProvider,
                this.EventHubReceiverFactory);
        }

        /// <summary>
        /// Gets the partition identifiers from Event Hubs.
        /// </summary>
        /// <returns>A task which resolves to the partition identifiers.</returns>
        protected virtual async Task<string[]> GetPartitionIdsAsync()
        {
            return await client.GetPartitionIdsAsync();
        }

        /// <summary>
        /// Creates and initializes an Event Hubs adapter factory.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The stream provider name.</param>
        /// <returns>The initialized adapter factory.</returns>
        public static EventHubAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetKeyedService<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<EventHubAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
