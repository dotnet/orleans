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
        private readonly FixedSizeObjectPool<FixedSizeBuffer> _bufferPool;
        private readonly TimePurgePredicate _timePurge;

        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings,
            SerializationManager serializationManager
        ) : this(providerSettings, serializationManager, CreateBufferPool(providerSettings))
        {
        }

        public EventHubQueueCacheFactory(EventHubStreamProviderSettings providerSettings, SerializationManager serializationManager,
            FixedSizeObjectPool<FixedSizeBuffer> bufferPool)
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

        public IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger cacheLogger)
        {
            var cache = CreateCache(checkpointer, cacheLogger, _bufferPool, _timePurge, _serializationManager);
            if (_providerSettings.AveragingCachePressureMonitorFlowControlThreshold.HasValue)
            {
                var avgMonitor = new AveragingCachePressureMonitor(_providerSettings.AveragingCachePressureMonitorFlowControlThreshold.Value, cacheLogger);
                cache.AddCachePressureMonitor(avgMonitor);
            }

            if (_providerSettings.SlowConsumingMonitorPressureWindowSize.HasValue
                || _providerSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
            {
                var slowConsumeMonitor = new SlowConsumingPressureMonitor(cacheLogger);
                if (_providerSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
                {
                    slowConsumeMonitor.FlowControlThreshold = _providerSettings.SlowConsumingMonitorFlowControlThreshold.Value;
                }
                if (_providerSettings.SlowConsumingMonitorPressureWindowSize.HasValue)
                {
                    slowConsumeMonitor.PressureWindowSize = _providerSettings.SlowConsumingMonitorPressureWindowSize.Value;
                }

                cache.AddCachePressureMonitor(slowConsumeMonitor);
            }
            return cache;
        }

        protected virtual IEventHubQueueCache CreateCache(IStreamQueueCheckpointer<string> checkpointer,
            Logger cacheLogger, FixedSizeObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge,
            SerializationManager serializationManager)
        {
            return new EventHubQueueCache(checkpointer, bufferPool, timePurge, cacheLogger, serializationManager);
        }
    }
}