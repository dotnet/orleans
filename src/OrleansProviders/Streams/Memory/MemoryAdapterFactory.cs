﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Memory
{
    /// <summary>
    /// Adapter factory for in memory stream provider.
    /// This factory acts as the adapter and the adapter factory.  The events are stored in an in-memory grain that 
    /// behaves as an event queue, this provider adapter is primarily used for testing
    /// </summary>
    public class MemoryAdapterFactory : IQueueAdapterFactory, IQueueAdapter, IQueueAdapterCache
    {
        private IStreamQueueMapper streamQueueMapper;
        private ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain> queueGrains;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private IStreamFailureHandler streamFailureHandler;
        private IServiceProvider serviceProvider;
        private MemoryAdapterConfig adapterConfig;
        private Logger logger;
        private String providerName;
        private IGrainFactory grainFactory;

        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        public string Name { get { return adapterConfig.StreamProviderName; } }

        /// <summary>
        /// Determines whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        public bool IsRewindable { get { return false; } }

        /// <summary>
        /// Direction of this queue adapter: Read, Write or ReadWrite.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadOnly; } }

        /// <summary>
        /// Creates a failure handler for a partition.
        /// </summary>
        protected Func<string, Task<IStreamFailureHandler>> StreamFailureHandlerFactory { get; set; }

        /// <summary>
        /// Factory initialization.
        /// </summary>
        /// <param name="providerConfig"></param>
        /// <param name="providerName"></param>
        /// <param name="log"></param>
        /// <param name="svcProvider"></param>
        public void Init(IProviderConfiguration providerConfig, string providerName, Logger log, IServiceProvider svcProvider)
        {
            this.logger = log;
            this.serviceProvider = svcProvider;
            this.providerName = providerName;
            this.queueGrains = new ConcurrentDictionary<QueueId, IMemoryStreamQueueGrain>();
            this.adapterConfig = new MemoryAdapterConfig(providerName);
            grainFactory = (IGrainFactory)serviceProvider.GetService(typeof(IGrainFactory));
            adapterConfig.PopulateFromProviderConfig(providerConfig);
            this.streamQueueMapper = new HashRingBasedStreamQueueMapper(adapterConfig.TotalQueueCount, adapterConfig.StreamProviderName);

            // 10 meg buffer pool.  10 1 meg blocks
            this.bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterConfig.CacheSizeMb, () => new FixedSizeBuffer(1 << 20));
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
            IQueueAdapterReceiver receiver = (IQueueAdapterReceiver)new MemoryAdapterReceiver(GetQueueGrain(queueId), logger);
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
            QueueId queueId;
            MemoryEventData eventData;
            try
            {
                queueId = streamQueueMapper.GetQueueForStream(streamGuid, streamNamespace);
                eventData = MemoryBatchContainer.ToMemoryEventData(streamGuid, streamNamespace, events, requestContext);
                IMemoryStreamQueueGrain queueGrain = GetQueueGrain(queueId);
                await queueGrain.Enqueue(eventData);
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
            return new MemoryPooledCache(bufferPool, logger.GetSubLogger("messagecache", "-"));
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
            JenkinsHash jenkinsHash = JenkinsHash.Factory.GetHashGenerator();
            int providerNameGuidHash = (int)jenkinsHash.ComputeHash(providerName);

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
