﻿using System;
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
using Microsoft.Azure.EventHubs;
using System.Collections.Concurrent;
using System.Linq;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderWithCreatedCacheList : PersistentStreamProvider<EHStreamProviderWithCreatedCacheList.AdapterFactory>
    {
        public class AdapterFactory : EventDataGeneratorStreamProvider.AdapterFactory
        {
            private readonly ConcurrentBag<QueueCacheForTesting> createdCaches = new ConcurrentBag<QueueCacheForTesting>();

            public AdapterFactory()
            {
                createdCaches = new ConcurrentBag<QueueCacheForTesting>();
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                var nodeConfig = this.serviceProvider.GetRequiredService<NodeConfiguration>();
                var eventHubPath = hubSettings.Path;
                var sharedDimensions = new EventHubMonitorAggregationDimensions(globalConfig, nodeConfig, eventHubPath);
                return new CacheFactoryForTesting(providerSettings, SerializationManager, this.createdCaches, sharedDimensions);
            }

            private class CacheFactoryForTesting : EventHubQueueCacheFactory
            {
                private readonly ConcurrentBag<QueueCacheForTesting> caches; 

                public CacheFactoryForTesting(EventHubStreamProviderSettings providerSettings,
                    SerializationManager serializationManager, ConcurrentBag<QueueCacheForTesting> caches, EventHubMonitorAggregationDimensions sharedDimensions,
                    Func<EventHubCacheMonitorDimensions, Logger, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                    Func<EventHubBlockPoolMonitorDimensions, Logger, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                    : base(providerSettings, serializationManager, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
                {
                    this.caches = caches;
                }

                private const int defaultMaxAddCount = 10;
                protected override IEventHubQueueCache CreateCache(string partition, EventHubStreamProviderSettings providerSettings, IStreamQueueCheckpointer<string> checkpointer,
                    Logger cacheLogger, IObjectPool<FixedSizeBuffer> bufferPool, string blockPoolId,  TimePurgePredicate timePurge,
                    SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer)
                {
                    var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
                    var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, cacheLogger, telemetryProducer);
                    //set defaultMaxAddCount to 10 so TryCalculateCachePressureContribution will start to calculate real contribution shortly
                    var cache = new QueueCacheForTesting(defaultMaxAddCount, checkpointer, new EventHubDataAdapter(serializationManager, bufferPool),
                        EventHubDataComparer.Instance, cacheLogger, new EventHubCacheEvictionStrategy(cacheLogger, timePurge, cacheMonitor, providerSettings.StatisticMonitorWriteInterval),
                        cacheMonitor, providerSettings.StatisticMonitorWriteInterval);
                    this.caches.Add(cache);
                    return cache;
                }
            }

            private class QueueCacheForTesting : EventHubQueueCache, IQueueFlowController
            {
                public bool IsUnderPressure { get; private set; }

                public QueueCacheForTesting(int defaultMaxAddCount, IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter,
                    ICacheDataComparer<CachedEventHubMessage> comparer, Logger logger, IEvictionStrategy<CachedEventHubMessage> evictionStrategy,
                    ICacheMonitor cacheMonitor, TimeSpan? cacheMonitorWriteInterval)
                    : base(defaultMaxAddCount, checkpointer, cacheDataAdapter, comparer, logger, evictionStrategy, cacheMonitor, cacheMonitorWriteInterval)
                {
                }

                int IQueueFlowController.GetMaxAddCount()
                {
                    int maxAddCount = base.GetMaxAddCount();
                    this.IsUnderPressure = maxAddCount <= 0;
                    return maxAddCount;
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
                        return Task.FromResult<object>(createdCaches.Any(cache => cache.IsUnderPressure));
                    default: return base.ExecuteCommand(command, arg);
                }
            }
        }
    }
}