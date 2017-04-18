using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceBus.Tests.TestStreamProviders
{
    internal class EHStreamProviderWithCreatedCacheList : PersistentStreamProvider<EHStreamProviderWithCreatedCacheList.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory, IControllable
        {
            private readonly List<IEventHubQueueCache> createdCaches;

            public AdapterFactory()
            {
                createdCaches = new List<IEventHubQueueCache>();
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                return new CacheFactoryForTesting(providerSettings, SerializationManager, createdCaches);
            }

            private class CacheFactoryForTesting : EventHubQueueCacheFactory
            {
                private readonly List<IEventHubQueueCache> _caches;

                public CacheFactoryForTesting(EventHubStreamProviderSettings providerSettings,
                    SerializationManager serializationManager, List<IEventHubQueueCache> caches)
                    : base(providerSettings, serializationManager)
                {
                    _caches = caches;
                }
                private const int defaultMaxAddCount = 2;
                protected override IEventHubQueueCache CreateCache(IStreamQueueCheckpointer<string> checkpointer, Logger cacheLogger,
                    IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge, SerializationManager serializationManager)
                {
                    //set defaultMaxAddCount to 2 so TryCalculateCachePressureContribution will start to calculate real contribution shortly.
                    var cache = new EventHubQueueCache(defaultMaxAddCount, checkpointer, new EventHubDataAdapter(serializationManager, bufferPool, timePurge), 
                        EventHubDataComparer.Instance, cacheLogger);
                    _caches.Add(cache);
                    return cache;
                }
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