﻿
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        private TSerializer serializer;
        private IStreamQueueMapper streamQueueMapper;
        private ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain> queueGrains;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private BlockPoolMonitorDimensions blockPoolMonitorDimensions;
        private MonitorAggregationDimensions sharedDimensions;
        private IStreamFailureHandler streamFailureHandler;
        private IServiceProvider serviceProvider;
        private MemoryAdapterConfig adapterConfig;
        private ITelemetryProducer telemetryProducer;
        private ILogger logger;
        private ILoggerFactory loggerFactory;
        private String providerName;
        private IGrainFactory grainFactory;
        private TimePurgePredicate purgePredicate;
        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name => adapterConfig.StreamProviderName;

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

        /// <summary>
        /// Factory initialization.
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="name"></param>
        /// <param name="svcProvider"></param>
        public void Init(IProviderConfiguration providerConfig, string name, IServiceProvider svcProvider)
        {
            logger = svcProvider.GetService<ILogger<MemoryAdapterFactory<TSerializer>>>();
            this.loggerFactory = svcProvider.GetRequiredService<ILoggerFactory>();
            serviceProvider = svcProvider;
            providerName = name;
            queueGrains = new ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain>();
            adapterConfig = new MemoryAdapterConfig(providerName);
            this.telemetryProducer = svcProvider.GetService<ITelemetryProducer>();
            if (CacheMonitorFactory == null)
                this.CacheMonitorFactory = (dimensions, telemetryProducer) => new DefaultCacheMonitor(dimensions, telemetryProducer);
            if (this.BlockPoolMonitorFactory == null)
                this.BlockPoolMonitorFactory = (dimensions, telemetryProducer) => new DefaultBlockPoolMonitor(dimensions, telemetryProducer);
            if (this.ReceiverMonitorFactory == null)
                this.ReceiverMonitorFactory = (dimensions, telemetryProducer) => new DefaultQueueAdapterReceiverMonitor(dimensions, telemetryProducer);
            purgePredicate = new TimePurgePredicate(adapterConfig.DataMinTimeInCache, adapterConfig.DataMaxAgeInCache);
            grainFactory = (IGrainFactory)serviceProvider.GetService(typeof(IGrainFactory));
            adapterConfig.PopulateFromProviderConfig(providerConfig);
            streamQueueMapper = new HashRingBasedStreamQueueMapper(adapterConfig.TotalQueueCount, adapterConfig.StreamProviderName);

            this.sharedDimensions = new MonitorAggregationDimensions(serviceProvider.GetService<GlobalConfiguration>(), serviceProvider.GetService<NodeConfiguration>());
            this.serializer = MemoryMessageBodySerializerFactory<TSerializer>.GetOrCreateSerializer(svcProvider);
        }

        private void CreateBufferPoolIfNotCreatedYet()
        {
            if (this.bufferPool == null)
            {
                // 1 meg block size pool
                this.blockPoolMonitorDimensions = new BlockPoolMonitorDimensions(this.sharedDimensions, $"BlockPool-{Guid.NewGuid()}");
                var oneMb = 1 << 20;
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(blockPoolMonitorDimensions, this.telemetryProducer), oneMb);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(oneMb), objectPoolMonitor, this.adapterConfig.StatisticMonitorWriteInterval);
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
        /// Creates a quere receiver for the specificed queueId
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            var dimensions = new ReceiverMonitorDimensions(this.sharedDimensions, queueId.ToString());
            var receiverLogger = this.loggerFactory.CreateLogger($"{typeof(MemoryAdapterReceiver<TSerializer>).FullName}.{this.providerName}.{queueId}");
            var receiverMonitor = this.ReceiverMonitorFactory(dimensions, this.telemetryProducer);
            IQueueAdapterReceiver receiver = new MemoryAdapterReceiver<TSerializer>(GetQueueGrain(queueId), receiverLogger, this.serializer, receiverMonitor);
            return receiver;
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
        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            try
            {
                var queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
                ArraySegment<byte> bodyBytes = serializer.Serialize(new MemoryMessageBody(events.Cast<object>(), requestContext));
                var messageData = MemoryMessageData.Create(streamGuid, streamNamespace, bodyBytes);
                IMemoryStreamQueueGrain queueGrain = GetQueueGrain(queueId);
                await queueGrain.Enqueue(messageData);
            }
            catch (Exception exc)
            {
                logger.Error((int)ProviderErrorCode.MemoryStreamProviderBase_QueueMessageBatchAsync, "Exception thrown in MemoryAdapterFactory.QueueMessageBatchAsync.", exc);
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
            var logger = this.loggerFactory.CreateLogger($"{typeof(MemoryPooledCache<TSerializer>).FullName}.{this.providerName}.{queueId}");
            var monitor = this.CacheMonitorFactory(new CacheMonitorDimensions(this.sharedDimensions, queueId.ToString(), this.blockPoolMonitorDimensions.BlockPoolId), this.telemetryProducer);
            return new MemoryPooledCache<TSerializer>(bufferPool, purgePredicate, logger, this.serializer, monitor, this.adapterConfig.StatisticMonitorWriteInterval);
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
            int providerNameGuidHash = (int)JenkinsHash.ComputeHash(providerName);

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
            return queueGrains.GetOrAdd(queueId, grainFactory.GetGrain<IMemoryStreamQueueGrain>(GenerateDeterministicGuid(queueId)));
        }
    }
}
