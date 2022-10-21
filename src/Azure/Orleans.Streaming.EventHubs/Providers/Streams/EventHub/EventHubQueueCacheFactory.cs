using System;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
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
        private readonly StreamCacheEvictionOptions evictionOptions;
        private readonly StreamStatisticOptions statisticOptions;
        private readonly IEventHubDataAdapter dataAdater;
        private readonly TimePurgePredicate timePurge;
        private readonly EventHubMonitorAggregationDimensions sharedDimensions;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private string bufferPoolId;

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
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, IBlockPoolMonitor> blockPoolMonitorFactory = null)
        {
            this.cacheOptions = cacheOptions;
            this.evictionOptions = evictionOptions;
            this.statisticOptions = statisticOptions;
            this.dataAdater = dataAdater;
            this.timePurge = new TimePurgePredicate(evictionOptions.DataMinTimeInCache, evictionOptions.DataMaxAgeInCache);
            this.sharedDimensions = sharedDimensions;
            this.CacheMonitorFactory = cacheMonitorFactory ?? ((dimensions, logger) => new DefaultEventHubCacheMonitor(dimensions));
            this.BlockPoolMonitorFactory = blockPoolMonitorFactory ?? ((dimensions, logger) => new DefaultEventHubBlockPoolMonitor(dimensions));
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
                var bufferSize = 1 << 20;
                this.bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, this.bufferPoolId);
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(monitorDimensions, loggerFactory), bufferSize);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize),
                    objectPoolMonitor, statisticOptions.StatisticMonitorWriteInterval);
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
            var evictionStrategy = new ChronologicalEvictionStrategy(logger, timePurge, cacheMonitor, statisticOptions.StatisticMonitorWriteInterval);
            return new EventHubQueueCache(partition, EventHubAdapterReceiver.MaxMessagesPerRead, bufferPool, dataAdatper, evictionStrategy, checkpointer, logger,  
                cacheMonitor, statisticOptions.StatisticMonitorWriteInterval, streamCacheEvictionOptions.MetadataMinTimeInCache);
        }
    }
}