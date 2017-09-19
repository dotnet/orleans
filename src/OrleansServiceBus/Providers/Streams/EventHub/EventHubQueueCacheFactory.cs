using System;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
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
        private readonly EventHubStreamProviderSettings providerSettings;
        private readonly SerializationManager serializationManager;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private string bufferPoolId;
        private readonly TimePurgePredicate timePurge;

        private EventHubMonitorAggregationDimensions sharedDimensions;

        /// <summary>
        /// Create a cache monitor to report performance metrics.
        /// Factory funciton should return an ICacheMonitor.
        /// </summary>
        public Func<EventHubCacheMonitorDimensions, Logger, ITelemetryProducer, ICacheMonitor> CacheMonitorFactory { set; get; }
        /// <summary>
        /// Create a block pool monitor to report performance metrics.
        /// Factory funciton should return an IObjectPoolMonitor.
        /// </summary>
        public Func<EventHubBlockPoolMonitorDimensions, Logger, ITelemetryProducer, IBlockPoolMonitor> BlockPoolMonitorFactory { set; get; }

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <param name="serializationManager"></param>
        /// <param name="sharedDimensions">shared dimensions between cache monitor and block pool monitor</param>
        /// <param name="cacheMonitorFactory"></param>
        /// <param name="blockPoolMonitorFactory"></param>
        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
            Func<EventHubCacheMonitorDimensions, Logger, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, Logger, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
        {
            this.providerSettings = providerSettings;
            this.serializationManager = serializationManager;
            this.timePurge = new TimePurgePredicate(this.providerSettings.DataMinTimeInCache, this.providerSettings.DataMaxAgeInCache);
            this.sharedDimensions = sharedDimensions;
            this.CacheMonitorFactory = cacheMonitorFactory == null?(dimensions, logger, telemetryProducer) => new DefaultEventHubCacheMonitor(dimensions, telemetryProducer) : cacheMonitorFactory;
            this.BlockPoolMonitorFactory = blockPoolMonitorFactory == null? (dimensions, logger, telemetryProducer) => new DefaultEventHubBlockPoolMonitor(dimensions, telemetryProducer) : blockPoolMonitorFactory;
        }

        /// <summary>
        /// Function which create an EventHubQueueCache, which by default will configure the EventHubQueueCache using configuration in CreateBufferPool function
        /// and AddCachePressureMonitors function.
        /// </summary>
        /// <returns></returns>
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger, ITelemetryProducer telemetryProducer)
        {
            string blockPoolId;
            var blockPool = CreateBufferPool(this.providerSettings, logger, this.sharedDimensions, telemetryProducer, out blockPoolId);
            var cache = CreateCache(partition, this.providerSettings, checkpointer, logger, blockPool, blockPoolId, this.timePurge, this.serializationManager, this.sharedDimensions, telemetryProducer);
            AddCachePressureMonitors(cache, this.providerSettings, logger);
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamProviderSettings providerSettings, Logger logger, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer, out string blockPoolId)
        {
            if (this.bufferPool == null)
            {
                var bufferSize = 1 << 20;
                this.bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, this.bufferPoolId);
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(monitorDimensions, logger, telemetryProducer), bufferSize);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize),
                    objectPoolMonitor, providerSettings.StatisticMonitorWriteInterval);
            }
            blockPoolId = this.bufferPoolId;
            return this.bufferPool;
        }

        /// <summary>
        /// Function used to configure cache pressure monitors for EventHubQueueCache. 
        /// User can override this function to provide more customization on cache pressure monitors
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="providerSettings"></param>
        /// <param name="cacheLogger"></param>
        protected virtual void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamProviderSettings providerSettings,
            Logger cacheLogger)
        {
            if (providerSettings.AveragingCachePressureMonitorFlowControlThreshold.HasValue)
            {
                var avgMonitor = new AveragingCachePressureMonitor(
                    providerSettings.AveragingCachePressureMonitorFlowControlThreshold.Value, cacheLogger);
                cache.AddCachePressureMonitor(avgMonitor);
            }

            if (providerSettings.SlowConsumingMonitorPressureWindowSize.HasValue
                || providerSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
            {
                var slowConsumeMonitor = new SlowConsumingPressureMonitor(cacheLogger);
                if (providerSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
                {
                    slowConsumeMonitor.FlowControlThreshold = providerSettings.SlowConsumingMonitorFlowControlThreshold.Value;
                }
                if (providerSettings.SlowConsumingMonitorPressureWindowSize.HasValue)
                {
                    slowConsumeMonitor.PressureWindowSize = providerSettings.SlowConsumingMonitorPressureWindowSize.Value;
                }

                cache.AddCachePressureMonitor(slowConsumeMonitor);
            }
        }

        /// <summary>
        /// Default function to be called to create an EventhubQueueCache in IEventHubQueueCacheFactory.CreateCache method. User can 
        /// override this method to add more customization.
        /// </summary>
        protected virtual IEventHubQueueCache CreateCache(string partition, EventHubStreamProviderSettings providerSettings, IStreamQueueCheckpointer<string> checkpointer,
            Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, string blockPoolId, TimePurgePredicate timePurge,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer)
        {
            var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, cacheLogger, telemetryProducer);
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger, serializationManager, cacheMonitor, providerSettings.StatisticMonitorWriteInterval);
        }
    }
}