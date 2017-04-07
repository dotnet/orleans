using Orleans.Providers;
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
    class EHStreamProviderWithCreatedCacheList : PersistentStreamProvider<EHStreamProviderWithCreatedCacheList.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory, IControllable
        {
            private List<IEventHubQueueCache> createdCaches;
            private FixedSizeObjectPool<FixedSizeBuffer> bufferPool;
            private TimePurgePredicate timePurge;
            private static int defaultMaxAddCount = 10;
            public AdapterFactory()
            {
                createdCaches = new List<IEventHubQueueCache>();
                CacheFactory = CreateQueueCache;
            }

            private IEventHubQueueCache CreateQueueCache(string partition, IStreamQueueCheckpointer<string> checkpointer, Logger cacheLogger)
            {
                // the same code block as with EventHubAdapterFactory to define default CacheFactory
                // except for at the end we put the created cache in CreatedCaches list
                if(this.bufferPool == null)
                    this.bufferPool = new FixedSizeObjectPool<FixedSizeBuffer>(adapterSettings.CacheSizeMb, () => new FixedSizeBuffer(1 << 20));
                if(this.timePurge == null)
                    this.timePurge = new TimePurgePredicate(adapterSettings.DataMinTimeInCache, adapterSettings.DataMaxAgeInCache);

                //ser defaultMaxAddCount to 10 so TryCalculateCachePressureContribution will start to calculate real contribution shortly.
                var cache = new EventHubQueueCache(defaultMaxAddCount, checkpointer, new EventHubDataAdapter(this.SerializationManager, bufferPool, timePurge), EventHubDataComparer.Instance, cacheLogger);
                if (adapterSettings.AveragingCachePressureMonitorFlowControlThreshold.HasValue)
                {
                    var avgMonitor = new AveragingCachePressureMonitor(adapterSettings.AveragingCachePressureMonitorFlowControlThreshold.Value, cacheLogger);
                    cache.AddCachePressureMonitor(avgMonitor);
                }
                if (adapterSettings.SlowConsumingMonitorPressureWindowSize.HasValue
                || adapterSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
                {

                    var slowConsumeMonitor = new SlowConsumingPressureMonitor(cacheLogger);
                    if (adapterSettings.SlowConsumingMonitorFlowControlThreshold.HasValue)
                        slowConsumeMonitor.FlowControlThreshold = adapterSettings.SlowConsumingMonitorFlowControlThreshold.Value;
                    if (adapterSettings.SlowConsumingMonitorPressureWindowSize.HasValue)
                        slowConsumeMonitor.PressureWindowSize = adapterSettings.SlowConsumingMonitorPressureWindowSize.Value;
                    cache.AddCachePressureMonitor(slowConsumeMonitor);
                }
                this.createdCaches.Add(cache);
                return cache;
            }

            public static int IsCacheBackPressureTriggeredCommand = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 3;
            /// <summary>
            /// Only command expecting: determine whether back pressure algorithm on any of the created caches
            /// is triggered.
            /// </summary>
            /// <param name="command"></param>
            /// <param name="arg"></param>
            /// <returns></returns>
            public Task<object> ExecuteCommand(int command, object arg)
            {
                foreach (var cache in this.createdCaches)
                {
                    if (cache.GetMaxAddCount() == 0)
                        return Task.FromResult<object>(true);
                }
                return Task.FromResult<object>(false);
            }
        }
    }
}
