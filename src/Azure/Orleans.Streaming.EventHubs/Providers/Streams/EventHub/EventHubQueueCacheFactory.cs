using System;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streaming.EventHubs.StatisticMonitors;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Factory class to configure and create IEventHubQueueCache
    /// </summary>
    public class EventHubQueueCacheFactory : IEventHubQueueCacheFactory
    {
        private readonly EventHubStreamCachePressureOptions cacheOptions;
        private readonly EventHubStreamCacheMemoryOptions cacheMemoryOptions;
        private readonly StreamCacheEvictionOptions evictionOptions;
        private readonly StreamStatisticOptions statisticOptions;
        private readonly IEventHubDataAdapter dataAdater;
        private readonly TimePurgePredicate timePurge;
        private readonly EventHubMonitorAggregationDimensions sharedDimensions;
        private readonly OrleansInstruments orleansInstruments;
        private volatile IObjectPool<FixedSizeBuffer> bufferPool = null!;
        private string bufferPoolId = null!;
        private readonly object bufferPoolLock = new();
        private readonly EventHubCacheMemoryController memoryController;

        /// <summary>
        /// Create a cache monitor to report performance metrics.
        /// Factory function should return an ICacheMonitor.
        /// </summary>
        public Func<EventHubCacheMonitorDimensions, ILoggerFactory, ICacheMonitor> CacheMonitorFactory { set; get; }

        /// <summary>
        /// Create a block pool monitor to report performance metrics.
        /// Factory function should return an IObjectPoolMonitor.
        /// </summary>
        public Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, IBlockPoolMonitor> BlockPoolMonitorFactory { set; get; }

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        public EventHubQueueCacheFactory(
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions evictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdater,
            EventHubMonitorAggregationDimensions sharedDimensions,
            OrleansInstruments instruments,
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ICacheMonitor>? cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, IBlockPoolMonitor>? blockPoolMonitorFactory = null)
            : this(
                cacheOptions,
                new EventHubStreamCacheMemoryOptions(),
                evictionOptions,
                statisticOptions,
                dataAdater,
                sharedDimensions,
                instruments,
                cacheMonitorFactory,
                blockPoolMonitorFactory)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubQueueCacheFactory"/> class.
        /// </summary>
        public EventHubQueueCacheFactory(
            EventHubStreamCachePressureOptions cacheOptions,
            EventHubStreamCacheMemoryOptions cacheMemoryOptions,
            StreamCacheEvictionOptions evictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdater,
            EventHubMonitorAggregationDimensions sharedDimensions,
            OrleansInstruments instruments,
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ICacheMonitor>? cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, IBlockPoolMonitor>? blockPoolMonitorFactory = null)
        {
            this.cacheOptions = cacheOptions;
            this.cacheMemoryOptions = cacheMemoryOptions;
            this.evictionOptions = evictionOptions;
            this.statisticOptions = statisticOptions;
            this.dataAdater = dataAdater;
            this.timePurge = new TimePurgePredicate(evictionOptions.DataMinTimeInCache, evictionOptions.DataMaxAgeInCache);
            this.sharedDimensions = sharedDimensions;
            this.orleansInstruments = instruments;
            this.memoryController = new EventHubCacheMemoryController(cacheMemoryOptions.MaxActiveCacheMemory);
            this.CacheMonitorFactory = cacheMonitorFactory ?? ((dimensions, logger) => new DefaultEventHubCacheMonitor(dimensions, this.orleansInstruments));
            this.BlockPoolMonitorFactory = blockPoolMonitorFactory ?? ((dimensions, logger) => new DefaultEventHubBlockPoolMonitor(dimensions, this.orleansInstruments));
        }

        /// <summary>
        /// Function which create an EventHubQueueCache, which by default will configure the EventHubQueueCache using configuration in CreateBufferPool function
        /// and AddCachePressureMonitors function.
        /// </summary>
        /// <returns></returns>
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory)
        {
            string blockPoolId;
            var blockPool = CreateBufferPool(this.statisticOptions, loggerFactory, this.sharedDimensions, out blockPoolId);
            var cache = CreateCache(partition, dataAdater, this.statisticOptions, this.evictionOptions, checkpointer, loggerFactory, blockPool, blockPoolId, this.timePurge, this.sharedDimensions);
            AddCachePressureMonitors(cache, this.cacheOptions, loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{this.sharedDimensions.EventHubPath}.{partition}"));
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(StreamStatisticOptions statisticOptions, ILoggerFactory loggerFactory, EventHubMonitorAggregationDimensions sharedDimensions, out string blockPoolId)
        {
            if (this.bufferPool == null)
            {
                lock (bufferPoolLock)
                {
                    if (this.bufferPool == null)
                    {
                        this.bufferPoolId = $"AdaptiveBlockPool-{Guid.NewGuid()}";
                        var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, this.bufferPoolId);
                        this.bufferPool = new EventHubCacheBufferPool(
                            this.memoryController,
                            this.cacheMemoryOptions.MaxBufferPoolMemory,
                            this.BlockPoolMonitorFactory(monitorDimensions, loggerFactory),
                            statisticOptions.StatisticMonitorWriteInterval);
                    }
                }
            }
            blockPoolId = this.bufferPoolId;
            return this.bufferPool;
        }

        /// <summary>
        /// Function used to configure cache pressure monitors for EventHubQueueCache. 
        /// User can override this function to provide more customization on cache pressure monitors
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="providerOptions"></param>
        /// <param name="cacheLogger"></param>
        protected virtual void AddCachePressureMonitors(
            IEventHubQueueCache cache,
            EventHubStreamCachePressureOptions providerOptions,
            ILogger cacheLogger)
        {
            if (providerOptions.AveragingCachePressureMonitorFlowControlThreshold.HasValue)
            {
                var avgMonitor = new AveragingCachePressureMonitor(
                    providerOptions.AveragingCachePressureMonitorFlowControlThreshold.Value, cacheLogger);
                cache.AddCachePressureMonitor(avgMonitor);
            }

            if (providerOptions.SlowConsumingMonitorPressureWindowSize.HasValue
                || providerOptions.SlowConsumingMonitorFlowControlThreshold.HasValue)
            {
                var slowConsumeMonitor = new SlowConsumingPressureMonitor(cacheLogger);
                if (providerOptions.SlowConsumingMonitorFlowControlThreshold.HasValue)
                {
                    slowConsumeMonitor.FlowControlThreshold = providerOptions.SlowConsumingMonitorFlowControlThreshold.Value;
                }
                if (providerOptions.SlowConsumingMonitorPressureWindowSize.HasValue)
                {
                    slowConsumeMonitor.PressureWindowSize = providerOptions.SlowConsumingMonitorPressureWindowSize.Value;
                }

                cache.AddCachePressureMonitor(slowConsumeMonitor);
            }
        }

        /// <summary>
        /// Default function to be called to create an EventhubQueueCache in IEventHubQueueCacheFactory.CreateCache method. User can 
        /// override this method to add more customization.
        /// </summary>
        protected virtual IEventHubQueueCache CreateCache(
            string partition,
            IEventHubDataAdapter dataAdatper,
            StreamStatisticOptions statisticOptions,
            StreamCacheEvictionOptions streamCacheEvictionOptions,
            IStreamQueueCheckpointer<string> checkpointer,
            ILoggerFactory loggerFactory,
            IObjectPool<FixedSizeBuffer> bufferPool,
            string blockPoolId,
            TimePurgePredicate timePurge,
            EventHubMonitorAggregationDimensions sharedDimensions)
        {
            var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, loggerFactory);
            var logger = loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{sharedDimensions.EventHubPath}.{partition}");
            var cacheMemoryController = bufferPool is IEventHubCacheBufferPool ? this.memoryController : null;
            var evictionStrategy = cacheMemoryController is null
                ? new ChronologicalEvictionStrategy(logger, timePurge, cacheMonitor, statisticOptions.StatisticMonitorWriteInterval)
                : new EventHubCacheEvictionStrategy(
                    logger,
                    timePurge,
                    cacheMonitor,
                    statisticOptions.StatisticMonitorWriteInterval);
            return new EventHubQueueCache(
                partition,
                EventHubAdapterReceiver.MaxMessagesPerRead,
                bufferPool,
                dataAdatper,
                evictionStrategy,
                checkpointer,
                logger,
                cacheMonitor,
                statisticOptions.StatisticMonitorWriteInterval,
                streamCacheEvictionOptions.MetadataMinTimeInCache,
                cacheMemoryController);
        }

        internal sealed class EventHubCacheEvictionStrategy(
            ILogger logger,
            TimePurgePredicate timePurge,
            ICacheMonitor? cacheMonitor,
            TimeSpan? monitorWriteInterval)
            : ChronologicalEvictionStrategy(logger, timePurge, cacheMonitor, monitorWriteInterval), IMemoryPressureEvictionStrategy
        {
            private bool purgingForMemoryPressure;
            private bool oldestBufferInitialized;
            private object? oldestBuffer;

            public void PerformMemoryPressurePurge(DateTime nowUtc)
            {
                purgingForMemoryPressure = true;
                oldestBufferInitialized = false;
                try
                {
                    PerformPurge(nowUtc);
                }
                finally
                {
                    purgingForMemoryPressure = false;
                    oldestBufferInitialized = false;
                    oldestBuffer = null;
                }
            }

            protected override bool ShouldPurge(
                ref CachedMessage cachedMessage,
                ref CachedMessage newestCachedMessage,
                DateTime nowUtc)
            {
                if (purgingForMemoryPressure)
                {
                    if (!oldestBufferInitialized)
                    {
                        oldestBuffer = cachedMessage.Segment.Array;
                        oldestBufferInitialized = true;
                    }

                    return ReferenceEquals(cachedMessage.Segment.Array, oldestBuffer);
                }

                return base.ShouldPurge(ref cachedMessage, ref newestCachedMessage, nowUtc);
            }
        }
    }
}
