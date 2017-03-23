using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderWithSlowConsumingPressureDetecting : PersistentStreamProvider<EHStreamProviderWithSlowConsumingPressureDetecting.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory
        {
            public AdapterFactory()
            {
                CacheFactory = CreateQueueCache;
            }

            private IEventHubQueueCache CreateQueueCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger log)
            {
                var bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterSettings.CacheSizeMb, () => new FixedSizeBuffer(1 << 20));
                var timePurge = new TimePurgePredicate(adapterSettings.DataMinTimeInCache, adapterSettings.DataMaxAgeInCache);
                var eventhubQueeuCache = new EventHubQueueCache(checkpointer, bufferPool, timePurge, log, this.SerializationManager);
                var slowConsumingPressureMonitor = new SlowConsumingPressureMonitor(0.5, log);
                eventhubQueeuCache.AddCachePressureMonitor(slowConsumingPressureMonitor);
                return eventhubQueeuCache;
            }
        }
    }
}
