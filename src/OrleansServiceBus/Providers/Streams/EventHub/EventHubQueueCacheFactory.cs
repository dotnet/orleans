using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    public class EventHubQueueCacheFactory : IEventHubQueueCacheFactory
    {
        private readonly EventHubStreamProviderSettings _providerSettings;
        private readonly SerializationManager _serializationManager;
        private readonly IObjectPool<FixedSizeBuffer> _bufferPool;
        private readonly TimePurgePredicate _timePurge;

        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager
        ) : this(providerSettings, serializationManager, CreateBufferPool(providerSettings))
        {
        }

        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager, IObjectPool<FixedSizeBuffer> bufferPool)
        {
            _providerSettings = providerSettings;
            _serializationManager = serializationManager;
            _bufferPool = bufferPool;
            _timePurge = new TimePurgePredicate(_providerSettings.DataMinTimeInCache, _providerSettings.DataMaxAgeInCache);
        }

        private static FixedSizeObjectPool<FixedSizeBuffer> CreateBufferPool(EventHubStreamProviderSettings providerSettings)
        {
            return new FixedSizeObjectPool<FixedSizeBuffer>(providerSettings.CacheSizeMb, () => new FixedSizeBuffer(1 << 20));
        }

        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger logger)
        {
            var cache = CreateCache(checkpointer, logger, _bufferPool, _timePurge, _serializationManager);
            AddCachePressureMonitors(cache, _providerSettings, logger);
            return cache;
        }

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

        protected virtual IEventHubQueueCache CreateCache(IStreamQueueCheckpointer<string> checkpointer,
            Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge,
            SerializationManager serializationManager)
        {
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger, serializationManager);
        }
    }
}