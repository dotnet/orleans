using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Factory class to configure and create IEventHubQueueCache
    /// </summary>
    public class EventHubQueueCacheFactory : IEventHubQueueCacheFactory
    {
        private readonly EventHubStreamProviderSettings _providerSettings;
        private readonly SerializationManager _serializationManager;
        private IObjectPool<FixedSizeBuffer> _bufferPool;
        private readonly TimePurgePredicate _timePurge;

        /// <summary>
        /// Constructor for EventHubQueueCacheFactory
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <param name="serializationManager"></param>
        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager
        )
        {
            _providerSettings = providerSettings;
            _serializationManager = serializationManager;
            _timePurge = new TimePurgePredicate(_providerSettings.DataMinTimeInCache, _providerSettings.DataMaxAgeInCache);
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
            var bufferPool = CreateBufferPool(_providerSettings);
            var cache = CreateCache(checkpointer, logger, bufferPool, _timePurge, _serializationManager);
            AddCachePressureMonitors(cache, _providerSettings, logger);
            return cache;
        }

        /// <summary>
        /// Function used to configure BufferPool for EventHubQueueCache. User can override this function to provide more customization on BufferPool creation
        /// </summary>
        /// <param name="providerSettings"></param>
        /// <returns></returns>
        protected virtual IObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamProviderSettings providerSettings)
        {
            return _bufferPool ?? (_bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(providerSettings.CacheSizeMb,
                () => new FixedSizeBuffer(1 << 20)));
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
        /// <param name="checkpointer"></param>
        /// <param name="cacheLogger"></param>
        /// <param name="bufferPool"></param>
        /// <param name="timePurge"></param>
        /// <param name="serializationManager"></param>
        /// <returns></returns>
        protected virtual IEventHubQueueCache CreateCache(IStreamQueueCheckpointer<string> checkpointer,
            Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge,
            SerializationManager serializationManager)
        {
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger, serializationManager);
        }
    }
}