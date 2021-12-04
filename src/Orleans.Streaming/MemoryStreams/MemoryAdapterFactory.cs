
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Configuration;

namespace Orleans.Providers
{
    /// <summary>
    /// Adapter factory for in memory stream provider.
    /// This factory acts as the adapter and the adapter factory.  The events are stored in an in-memory grain that 
    /// behaves as an event queue, this provider adapter is primarily used for testing
    /// </summary>
    public class MemoryAdapterFactory<TSerializer> : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
        where TSerializer : class, IMemoryMessageBodySerializer
    {
        private readonly StreamCacheEvictionOptions cacheOptions;
        private readonly StreamStatisticOptions statisticOptions;
        private readonly HashRingStreamQueueMapperOptions queueMapperOptions;
        private readonly IGrainFactory grainFactory;
        private readonly ITelemetryProducer telemetryProducer;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly TSerializer serializer;
        private IStreamQueueMapper streamQueueMapper;
        private ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain> queueGrains;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private BlockPoolMonitorDimensions blockPoolMonitorDimensions;
        private IStreamFailureHandler streamFailureHandler;
        private TimePurgePredicate purgePredicate;

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
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }

        /// <summary>
        /// Create a cache monitor to report cache related metrics
        /// Return a ICacheMonitor
        /// </summary>
        protected Func<CacheMonitorDimensions, ITelemetryProducer, ICacheMonitor> CacheMonitorFactory;

        /// <summary>
        /// Create a block pool monitor to monitor block pool related metrics
        /// Return a IBlockPoolMonitor
        /// </summary>
        protected Func<BlockPoolMonitorDimensions, ITelemetryProducer, IBlockPoolMonitor> BlockPoolMonitorFactory;

        /// <summary>
        /// Create a monitor to monitor QueueAdapterReceiver related metrics
        /// Return a IQueueAdapterReceiverMonitor
        /// </summary>
        protected Func<ReceiverMonitorDimensions, ITelemetryProducer, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;

        public MemoryAdapterFactory(string providerName, StreamCacheEvictionOptions cacheOptions, StreamStatisticOptions statisticOptions, HashRingStreamQueueMapperOptions queueMapperOptions,
            IServiceProvider serviceProvider, IGrainFactory grainFactory, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
        {
            this.Name = providerName;
            this.queueMapperOptions = queueMapperOptions ?? throw new ArgumentNullException(nameof(queueMapperOptions));
            this.cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentException(nameof(statisticOptions));
            this.grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            this.telemetryProducer = telemetryProducer ?? throw new ArgumentNullException(nameof(telemetryProducer));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = loggerFactory.CreateLogger<ILogger<MemoryAdapterFactory<TSerializer>>>();
            this.serializer = MemoryMessageBodySerializerFactory<TSerializer>.GetOrCreateSerializer(serviceProvider);
        }

        /// <summary>
        /// Factory initialization.
        /// </summary>
        public void Init()
        {
            this.queueGrains = new ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain>();
            if (CacheMonitorFactory == null)
                this.CacheMonitorFactory = (dimensions, telemetryProducer) => new DefaultCacheMonitor(dimensions, telemetryProducer);
            if (this.BlockPoolMonitorFactory == null)
                this.BlockPoolMonitorFactory = (dimensions, telemetryProducer) => new DefaultBlockPoolMonitor(dimensions, telemetryProducer);
            if (this.ReceiverMonitorFactory == null)
                this.ReceiverMonitorFactory = (dimensions, telemetryProducer) => new DefaultQueueAdapterReceiverMonitor(dimensions, telemetryProducer);
            this.purgePredicate = new TimePurgePredicate(this.cacheOptions.DataMinTimeInCache, this.cacheOptions.DataMaxAgeInCache);
            this.streamQueueMapper = new HashRingBasedStreamQueueMapper(this.queueMapperOptions, this.Name);
        }

        private void CreateBufferPoolIfNotCreatedYet()
        {
            if (this.bufferPool == null)
            {
                // 1 meg block size pool
                this.blockPoolMonitorDimensions = new BlockPoolMonitorDimensions($"BlockPool-{Guid.NewGuid()}");
                var oneMb = 1 << 20;
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(blockPoolMonitorDimensions, this.telemetryProducer), oneMb);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor, this.statisticOptions.StatisticMonitorWriteInterval);
            }
        }

        /// <summary>
        /// Create queue adapter.
        /// </summary>
        /// <returns></returns>
        public Task<IQueueAdapter> CreateAdapter()
        {
            return Task.FromResult<IQueueAdapter>(this);
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
            return streamQueueMapper;
        }

        /// <summary>
        /// Creates a queue receiver for the specified queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            var dimensions = new ReceiverMonitorDimensions(queueId.ToString());
            var receiverLogger = this.loggerFactory.CreateLogger($"{typeof(MemoryAdapterReceiver<TSerializer>).FullName}.{this.Name}.{queueId}");
            var receiverMonitor = this.ReceiverMonitorFactory(dimensions, this.telemetryProducer);
            IQueueAdapterReceiver receiver = new MemoryAdapterReceiver<TSerializer>(GetQueueGrain(queueId), receiverLogger, this.serializer, receiverMonitor);
            return receiver;
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
        public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            try
            {
                var queueId = streamQueueMapper.GetQueueForStream(streamId);
                ArraySegment<byte> bodyBytes = serializer.Serialize(new MemoryMessageBody(events.Cast<object>(), requestContext));
                var messageData = MemoryMessageData.Create(streamId, bodyBytes);
                IMemoryStreamQueueGrain queueGrain = GetQueueGrain(queueId);
                await queueGrain.Enqueue(messageData);
            }
            catch (Exception exc)
            {
                logger.LogError((int)ProviderErrorCode.MemoryStreamProviderBase_QueueMessageBatchAsync, exc, "Exception thrown in MemoryAdapterFactory.QueueMessageBatchAsync.");
                throw;
            }
        }

        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="queueId"></param>
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            //move block pool creation from init method to here, to avoid unnecessary block pool creation when stream provider is initialized in client side. 
            CreateBufferPoolIfNotCreatedYet();
            var logger = this.loggerFactory.CreateLogger($"{typeof(MemoryPooledCache<TSerializer>).FullName}.{this.Name}.{queueId}");
            var monitor = this.CacheMonitorFactory(new CacheMonitorDimensions(queueId.ToString(), this.blockPoolMonitorDimensions.BlockPoolId), this.telemetryProducer);
            return new MemoryPooledCache<TSerializer>(bufferPool, purgePredicate, logger, this.serializer, monitor, this.statisticOptions.StatisticMonitorWriteInterval);
        }

        /// <summary>
        /// Acquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return Task.FromResult(streamFailureHandler ?? (streamFailureHandler = new NoOpStreamDeliveryFailureHandler()));
        }

        /// <summary>
        /// Generate a deterministic Guid from a queue Id. 
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        private Guid GenerateDeterministicGuid(QueueId queueId)
        {
            // provider name hash code
            int providerNameGuidHash = (int)JenkinsHash.ComputeHash(this.Name);

            // get queueId hash code
            uint queueIdHash = queueId.GetUniformHashCode();
            byte[] queIdHashByes = BitConverter.GetBytes(queueIdHash);
            short s1 = BitConverter.ToInt16(queIdHashByes, 0);
            short s2 = BitConverter.ToInt16(queIdHashByes, 2);

            // build guid tailing 8 bytes from providerNameGuidHash and queIdHashByes.
            var tail = new List<byte>();
            tail.AddRange(BitConverter.GetBytes(providerNameGuidHash));
            tail.AddRange(queIdHashByes);

            // make guid.
            // - First int is provider name hash
            // - Two shorts from queue Id hash
            // - 8 byte tail from provider name hash and queue Id hash.
            return new Guid(providerNameGuidHash, s1, s2, tail.ToArray());
        }

        /// <summary>
        /// Get a MemoryStreamQueueGrain instance by queue Id. 
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        private IMemoryStreamQueueGrain GetQueueGrain(QueueId queueId)
        {
            return queueGrains.GetOrAdd(queueId, (id, arg) => arg.grainFactory.GetGrain<IMemoryStreamQueueGrain>(arg.instance.GenerateDeterministicGuid(id)), (instance: this, grainFactory));
        }

        public static MemoryAdapterFactory<TSerializer> Create(IServiceProvider services, string name)
        {
            var cachePurgeOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var queueMapperOptions = services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);
            var factory = ActivatorUtilities.CreateInstance<MemoryAdapterFactory<TSerializer>>(services, name, cachePurgeOptions, statisticOptions, queueMapperOptions);
            factory.Init();
            return factory;
        }
    }
}
