using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

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
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly TSerializer serializer;
        private IStreamQueueMapper streamQueueMapper;
        private ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain> queueGrains;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private BlockPoolMonitorDimensions blockPoolMonitorDimensions;
        private IStreamFailureHandler streamFailureHandler;
        private TimePurgePredicate purgePredicate;

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public bool IsRewindable => true;

        /// <inheritdoc />
        public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }

        /// <summary>
        /// Create a cache monitor to report cache related metrics
        /// Return a ICacheMonitor
        /// </summary>
        protected Func<CacheMonitorDimensions, ICacheMonitor> CacheMonitorFactory;

        /// <summary>
        /// Create a block pool monitor to monitor block pool related metrics
        /// Return a IBlockPoolMonitor
        /// </summary>
        protected Func<BlockPoolMonitorDimensions, IBlockPoolMonitor> BlockPoolMonitorFactory;

        /// <summary>
        /// Create a monitor to monitor QueueAdapterReceiver related metrics
        /// Return a IQueueAdapterReceiverMonitor
        /// </summary>
        protected Func<ReceiverMonitorDimensions, IQueueAdapterReceiverMonitor> ReceiverMonitorFactory;

        public MemoryAdapterFactory(
            string providerName,
            StreamCacheEvictionOptions cacheOptions,
            StreamStatisticOptions statisticOptions,
            HashRingStreamQueueMapperOptions queueMapperOptions,
            IServiceProvider serviceProvider,
            IGrainFactory grainFactory,
            ILoggerFactory loggerFactory)
        {
            this.Name = providerName;
            this.queueMapperOptions = queueMapperOptions ?? throw new ArgumentNullException(nameof(queueMapperOptions));
            this.cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
            this.statisticOptions = statisticOptions ?? throw new ArgumentException(nameof(statisticOptions));
            this.grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = loggerFactory.CreateLogger<ILogger<MemoryAdapterFactory<TSerializer>>>();
            this.serializer = MemoryMessageBodySerializerFactory<TSerializer>.GetOrCreateSerializer(serviceProvider);
        }

        /// <summary>
        /// Initializes this instance.
        /// </summary>
        public void Init()
        {
            this.queueGrains = new ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain>();
            if (CacheMonitorFactory == null)
                this.CacheMonitorFactory = (dimensions) => new DefaultCacheMonitor(dimensions);
            if (this.BlockPoolMonitorFactory == null)
                this.BlockPoolMonitorFactory = (dimensions) => new DefaultBlockPoolMonitor(dimensions);
            if (this.ReceiverMonitorFactory == null)
                this.ReceiverMonitorFactory = (dimensions) => new DefaultQueueAdapterReceiverMonitor(dimensions);
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
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(blockPoolMonitorDimensions), oneMb);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor, this.statisticOptions.StatisticMonitorWriteInterval);
            }
        }

        /// <inheritdoc />
        public Task<IQueueAdapter> CreateAdapter()
        {
            return Task.FromResult<IQueueAdapter>(this);
        }

        /// <inheritdoc />
        public IQueueAdapterCache GetQueueAdapterCache()
        {
            return this;
        }

        /// <inheritdoc />
        public IStreamQueueMapper GetStreamQueueMapper()
        {
            return streamQueueMapper;
        }

        /// <inheritdoc />
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            var dimensions = new ReceiverMonitorDimensions(queueId.ToString());
            var receiverLogger = this.loggerFactory.CreateLogger($"{typeof(MemoryAdapterReceiver<TSerializer>).FullName}.{this.Name}.{queueId}");
            var receiverMonitor = this.ReceiverMonitorFactory(dimensions);
            IQueueAdapterReceiver receiver = new MemoryAdapterReceiver<TSerializer>(GetQueueGrain(queueId), receiverLogger, this.serializer, receiverMonitor);
            return receiver;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public IQueueCache CreateQueueCache(QueueId queueId)
        {
            //move block pool creation from init method to here, to avoid unnecessary block pool creation when stream provider is initialized in client side. 
            CreateBufferPoolIfNotCreatedYet();
            var logger = this.loggerFactory.CreateLogger($"{typeof(MemoryPooledCache<TSerializer>).FullName}.{this.Name}.{queueId}");
            var monitor = this.CacheMonitorFactory(new CacheMonitorDimensions(queueId.ToString(), this.blockPoolMonitorDimensions.BlockPoolId));
            return new MemoryPooledCache<TSerializer>(bufferPool, purgePredicate, logger, this.serializer, monitor, this.statisticOptions.StatisticMonitorWriteInterval);
        }

        /// <inheritdoc />
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
        {
            return Task.FromResult(streamFailureHandler ?? (streamFailureHandler = new NoOpStreamDeliveryFailureHandler()));
        }

        /// <summary>
        /// Generate a deterministic Guid from a queue Id. 
        /// </summary>
        private Guid GenerateDeterministicGuid(QueueId queueId)
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, JenkinsHash.ComputeHash(Name));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], queueId.GetUniformHashCode());
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], queueId.GetNumericId());
            return new(bytes);
        }

        /// <summary>
        /// Get a MemoryStreamQueueGrain instance by queue Id. 
        /// </summary>
        private IMemoryStreamQueueGrain GetQueueGrain(QueueId queueId)
        {
            return queueGrains.GetOrAdd(queueId, (id, arg) => arg.grainFactory.GetGrain<IMemoryStreamQueueGrain>(arg.instance.GenerateDeterministicGuid(id)), (instance: this, grainFactory));
        }

        /// <summary>
        /// Creates a new <see cref="MemoryAdapterFactory{TSerializer}"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>A mew <see cref="MemoryAdapterFactory{TSerializer}"/> instance.</returns>
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
