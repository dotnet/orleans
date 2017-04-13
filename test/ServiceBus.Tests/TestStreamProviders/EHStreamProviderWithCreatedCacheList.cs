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
                return new CacheFactory(providerSettings, SerializationManager, createdCaches);
            }

            private class CacheFactory : EventHubQueueCacheFactory
            {
                private readonly List<IEventHubQueueCache> _caches;

                public CacheFactory(EventHubStreamProviderSettings providerSettings,
                    SerializationManager serializationManager, List<IEventHubQueueCache> caches)
                    : base(providerSettings, serializationManager)
                {
                    _caches = caches;
                }

                protected override IEventHubQueueCache CreateCache(IStreamQueueCheckpointer<string> checkpointer, Logger cacheLogger,
                    IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge, SerializationManager serializationManager)
                {
                    var cache = base.CreateCache(checkpointer, cacheLogger, bufferPool, timePurge, serializationManager);
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