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
        private readonly TimePurgePredicate timePurge;

        private EventHubMonitorAggregationDimentions sharedDimentions;
        /// <summary>
        /// Create a cache monitor to report performance metrics.
        /// Factory funciton should return an ICacheMonitor.
        /// </summary>
        public Func<EventHubCacheMonitorDimentions, Logger, ICacheMonitor> CacheMonitorFactory { set; get; }
        /// <summary>
        /// Create a block pool monitor to report performance metrics.
        /// Factory funciton should return an IObjectPoolMonitor.
        /// </summary>
        public Func<EventHubObjectPoolMonitorDimentions, Logger, IObjectPoolMonitor> ObjectPoolMonitorFacrtory { set; get; }

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <param name="serializationManager"></param>
        /// <param name="sharedDimentions">shared dimentions between cache monitor and block pool monitor</param>
        /// <param name="cacheMonitorFactory"></param>
        /// <param name="objectPoolMonitorFactory"></param>
        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager, EventHubMonitorAggregationDimentions sharedDimentions,
            Func<EventHubCacheMonitorDimentions, Logger, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubObjectPoolMonitorDimentions, Logger, IObjectPoolMonitor> objectPoolMonitorFactory = null)
        {
            this.providerSettings = providerSettings;
            this.serializationManager = serializationManager;
            this.timePurge = new TimePurgePredicate(this.providerSettings.DataMinTimeInCache, this.providerSettings.DataMaxAgeInCache);
            this.sharedDimentions = sharedDimentions;
            this.CacheMonitorFactory = cacheMonitorFactory == null?(dimentions, logger) => new DefaultEventHubCacheMonitor(dimentions, logger) : cacheMonitorFactory;
            this.ObjectPoolMonitorFacrtory = objectPoolMonitorFactory == null?(dimentions, logger) => new DefaultEventHubObjectPoolMonitor(dimentions, logger) : objectPoolMonitorFactory;
        }

        /// <summary>
        /// Function which create an EventHubQueueCache, which by default will configure the EventHubQueueCache using configuration in CreateBufferPool function
        /// and AddCachePressureMonitors function.
        /// </summary>
        /// <param name="partition"></param>
        /// <param name="checkpointer"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger)
        {
            var blockPool = CreateBufferPool(this.providerSettings, logger, this.sharedDimentions);
            var cache = CreateCache(partition, this.providerSettings, checkpointer, logger, blockPool, this.timePurge, this.serializationManager, this.sharedDimentions);
            AddCachePressureMonitors(cache, this.providerSettings, logger);
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <returns></returns>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamProviderSettings providerSettings, Logger logger, EventHubMonitorAggregationDimentions sharedDimentions)
        {
            if (this.bufferPool == null)
            {
                var blockPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{1<<20}";
                var monitorDimentions = new EventHubObjectPoolMonitorDimentions(sharedDimentions, blockPoolId);
                var blockPoolMonitor = this.ObjectPoolMonitorFacrtory(monitorDimentions, logger);
                this.bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(1 << 20), blockPoolId,
                    providerSettings.CacheSizeMb,
                    blockPoolMonitor, providerSettings.StatisticMonitorWriteInterval);
            }
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
        /// <param name="partition"></param>
        /// <param name="providerSettings"></param>
        /// <param name="checkpointer"></param>
        /// <param name="cacheLogger"></param>
        /// <param name="bufferPool"></param>
        /// <param name="timePurge"></param>
        /// <param name="serializationManager"></param>
        /// <param name="sharedDimentions"></param>
        /// <returns></returns>
        protected virtual IEventHubQueueCache CreateCache(string partition, EventHubStreamProviderSettings providerSettings, IStreamQueueCheckpointer<string> checkpointer,
            Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge,
            SerializationManager serializationManager, EventHubMonitorAggregationDimentions sharedDimentions)
        {
            var cacheMonitorDimentions = new EventHubCacheMonitorDimentions(sharedDimentions, partition, bufferPool.Id);
            var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimentions, cacheLogger);
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger, serializationManager, cacheMonitor, providerSettings.StatisticMonitorWriteInterval);
        }
    }
}