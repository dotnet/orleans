using System;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using OrleansServiceBus.Providers.Streams.EventHub.StatisticMonitors;

namespace Orleans.ServiceBus.Providers
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
        private readonly SerializationManager serializationManager;
        private readonly TimePurgePredicate timePurge;
        private readonly EventHubMonitorAggregationDimensions sharedDimensions;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private string bufferPoolId;

        /// <summary>
        /// Create a cache monitor to report performance metrics.
        /// Factory function should return an ICacheMonitor.
        /// </summary>
        public Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> CacheMonitorFactory { set; get; }

        /// <summary>
        /// Create a block pool monitor to report performance metrics.
        /// Factory function should return an IObjectPoolMonitor.
        /// </summary>
        public Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> BlockPoolMonitorFactory { set; get; }

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        public EventHubQueueCacheFactory(
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions evictionOptions, 
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdater,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
        {
            this.cacheOptions = cacheOptions;
            this.evictionOptions = evictionOptions;
            this.statisticOptions = statisticOptions;
            this.dataAdater = dataAdater;
            this.serializationManager = serializationManager;
            this.timePurge = new TimePurgePredicate(evictionOptions.DataMinTimeInCache, evictionOptions.DataMaxAgeInCache);
            this.sharedDimensions = sharedDimensions;
            this.CacheMonitorFactory = cacheMonitorFactory ?? ((dimensions, logger, telemetryProducer) => new DefaultEventHubCacheMonitor(dimensions, telemetryProducer));
            this.BlockPoolMonitorFactory = blockPoolMonitorFactory ?? ((dimensions, logger, telemetryProducer) => new DefaultEventHubBlockPoolMonitor(dimensions, telemetryProducer));
        }

        /// <summary>
        /// Function which create an EventHubQueueCache, which by default will configure the EventHubQueueCache using configuration in CreateBufferPool function
        /// and AddCachePressureMonitors function.
        /// </summary>
        /// <returns></returns>
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer)
        {
            string blockPoolId;
            var blockPool = CreateBufferPool(this.statisticOptions, loggerFactory, this.sharedDimensions, telemetryProducer, out blockPoolId);
            var cache = CreateCache(partition, dataAdater, this.statisticOptions, this.evictionOptions, checkpointer, loggerFactory, blockPool, blockPoolId, this.timePurge, this.serializationManager, this.sharedDimensions, telemetryProducer);
            AddCachePressureMonitors(cache, this.cacheOptions, loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{this.sharedDimensions.EventHubPath}.{partition}"));
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(StreamStatisticOptions statisticOptions, ILoggerFactory loggerFactory, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer, out string blockPoolId)
        {
            if (this.bufferPool == null)
            {
                var bufferSize = 1 << 20;
                this.bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, this.bufferPoolId);
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(monitorDimensions, loggerFactory, telemetryProducer), bufferSize);
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
        protected virtual void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamCachePressureOptions providerOptions,
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
            SerializationManager serializationManager,
            EventHubMonitorAggregationDimensions sharedDimensions,
            ITelemetryProducer telemetryProducer)
        {
            var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, loggerFactory, telemetryProducer);
            var logger = loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{sharedDimensions.EventHubPath}.{partition}");
            var evictionStrategy = new ChronologicalEvictionStrategy(logger, timePurge, cacheMonitor, statisticOptions.StatisticMonitorWriteInterval);
            return new EventHubQueueCache(partition, EventHubAdapterReceiver.MaxMessagesPerRead, bufferPool, dataAdatper, evictionStrategy, checkpointer, logger,  
                cacheMonitor, statisticOptions.StatisticMonitorWriteInterval, streamCacheEvictionOptions.MetadataMinTimeInCache);
        }
    }
}