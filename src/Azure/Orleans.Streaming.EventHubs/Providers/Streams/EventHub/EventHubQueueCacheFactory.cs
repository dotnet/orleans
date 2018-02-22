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
        private readonly EventHubStreamOptions options;
        private readonly SerializationManager serializationManager;
        private IObjectPool<FixedSizeBuffer> bufferPool;
        private string bufferPoolId;
        private readonly TimePurgePredicate timePurge;
        private EventHubMonitorAggregationDimensions sharedDimensions;

        /// <summary>
        /// Create a cache monitor to report performance metrics.
        /// Factory funciton should return an ICacheMonitor.
        /// </summary>
        public Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> CacheMonitorFactory { set; get; }
        /// <summary>
        /// Create a block pool monitor to report performance metrics.
        /// Factory funciton should return an IObjectPoolMonitor.
        /// </summary>
        public Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> BlockPoolMonitorFactory { set; get; }

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        /// <param name="options"></param>
        /// <param name="serializationManager"></param>
        /// <param name="sharedDimensions">shared dimensions between cache monitor and block pool monitor</param>
        /// <param name="cacheMonitorFactory"></param>
        /// <param name="blockPoolMonitorFactory"></param>
        public EventHubQueueCacheFactory(EventHubStreamOptions options,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
            ILoggerFactory loggerFactory,
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
        {
            this.options = options;
            this.serializationManager = serializationManager;
            this.timePurge = new TimePurgePredicate(this.options.DataMinTimeInCache, this.options.DataMaxAgeInCache);
            this.sharedDimensions = sharedDimensions;
            this.CacheMonitorFactory = cacheMonitorFactory == null?(dimensions, logger, telemetryProducer) => new DefaultEventHubCacheMonitor(dimensions, telemetryProducer) : cacheMonitorFactory;
            this.BlockPoolMonitorFactory = blockPoolMonitorFactory == null? (dimensions, logger, telemetryProducer) => new DefaultEventHubBlockPoolMonitor(dimensions, telemetryProducer) : blockPoolMonitorFactory;
        }

        /// <summary>
        /// Function which create an EventHubQueueCache, which by default will configure the EventHubQueueCache using configuration in CreateBufferPool function
        /// and AddCachePressureMonitors function.
        /// </summary>
        /// <returns></returns>
        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer)
        {
            string blockPoolId;
            var blockPool = CreateBufferPool(this.options, loggerFactory, this.sharedDimensions, telemetryProducer, out blockPoolId);
            var cache = CreateCache(partition, this.options, checkpointer, loggerFactory, blockPool, blockPoolId, this.timePurge, this.serializationManager, this.sharedDimensions, telemetryProducer);
            AddCachePressureMonitors(cache, this.options, loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{this.sharedDimensions.EventHubPath}.{partition}"));
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamOptions providerOptions, ILoggerFactory loggerFactory, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer, out string blockPoolId)
        {
            if (this.bufferPool == null)
            {
                var bufferSize = 1 << 20;
                this.bufferPoolId = $"BlockPool-{new Guid().ToString()}-BlockSize-{bufferSize}";
                var monitorDimensions = new EventHubBlockPoolMonitorDimensions(sharedDimensions, this.bufferPoolId);
                var objectPoolMonitor = new ObjectPoolMonitorBridge(this.BlockPoolMonitorFactory(monitorDimensions, loggerFactory, telemetryProducer), bufferSize);
                this.bufferPool = new ObjectPool<FixedSizeBuffer>(() => new FixedSizeBuffer(bufferSize),
                    objectPoolMonitor, providerOptions.StatisticMonitorWriteInterval);
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
        protected virtual void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamOptions providerOptions,
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
        protected virtual IEventHubQueueCache CreateCache(string partition, EventHubStreamOptions providerOptions, IStreamQueueCheckpointer<string> checkpointer,
            ILoggerFactory loggerFactory, IObjectPool<FixedSizeBuffer> bufferPool, string blockPoolId, TimePurgePredicate timePurge,
            SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer)
        {
            var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
            var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, loggerFactory, telemetryProducer);
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{sharedDimensions.EventHubPath}.{partition}"), serializationManager, cacheMonitor, providerOptions.StatisticMonitorWriteInterval);
        }
    }
}