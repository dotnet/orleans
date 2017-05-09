using System;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.ServiceBus.Providers.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;

namespace ServiceBus.Tests.TestStreamProviders
{
    internal class EHStreamProviderWithCreatedCacheList : PersistentStreamProvider<EHStreamProviderWithCreatedCacheList.AdapterFactory>
    {
        public class AdapterFactory : EventDataGeneratorStreamProvider.AdapterFactory
        {
            private readonly List<IEventHubQueueCache> createdCaches;

            public AdapterFactory()
            {
                createdCaches = new List<IEventHubQueueCache>();
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var globalConfig = this.serviceProvider
                    .GetRequiredService<Func<GlobalConfiguration>>().Invoke();
                var nodeConfig = this.serviceProvider.GetRequiredService<Func<NodeConfiguration>>()
                    .Invoke();
                var eventHubPath = hubSettings.Path;
                var sharedDimentions = new EventHubMonitorAggregationDimentions(globalConfig, nodeConfig, eventHubPath);
                return new CacheFactoryForTesting(providerSettings, SerializationManager, createdCaches, sharedDimentions);
            }

            private class CacheFactoryForTesting : EventHubQueueCacheFactory
            {
                private readonly List<IEventHubQueueCache> caches;

                public CacheFactoryForTesting(EventHubStreamProviderSettings providerSettings,
                    SerializationManager serializationManager, List<IEventHubQueueCache> caches, EventHubMonitorAggregationDimentions sharedDimentions)
                    : base(providerSettings, serializationManager, sharedDimentions)
                {
                    this.caches = caches;
                }
                private const int defaultMaxAddCount = 10;
                protected override IEventHubQueueCache CreateCache(string partition, EventHubStreamProviderSettings providerSettings, IStreamQueueCheckpointer<string> checkpointer,
                    Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, TimePurgePredicate timePurge,
                    SerializationManager serializationManager, EventHubMonitorAggregationDimentions sharedDimentions)
                {
                    var cacheMonitorDimentions = new EventHubCacheMonitorDimentions(sharedDimentions, partition, bufferPool.Id);
                    var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimentions, cacheLogger);
                    //set defaultMaxAddCount to 10 so TryCalculateCachePressureContribution will start to calculate real contribution shortly
                    var cache = new EventHubQueueCache(defaultMaxAddCount, checkpointer, new EventHubDataAdapter(serializationManager, bufferPool), 
                        EventHubDataComparer.Instance, cacheLogger, new EventHubCacheEvictionStrategy(cacheLogger, cacheMonitor, providerSettings.StatisticMonitorWriteInterval, timePurge));
                    this.caches.Add(cache);
                    return cache;
                }
            }

            public const int IsCacheBackPressureTriggeredCommand = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 3;

            /// <summary>
            /// Only command expecting: determine whether back pressure algorithm on any of the created caches
            /// is triggered.
            /// </summary>
            /// <param name="command"></param>
            /// <param name="arg"></param>
            /// <returns></returns>
            public override Task<object> ExecuteCommand(int command, object arg)
            {
                switch (command)
                {
                    case IsCacheBackPressureTriggeredCommand:
                        foreach (var cache in this.createdCaches)
                        {
                            if (cache.GetMaxAddCount() == 0)
                                return Task.FromResult<object>(true);
                        }
                        return Task.FromResult<object>(false);
                    default: return base.ExecuteCommand(command, arg);
                }

            }
        }
    }
}