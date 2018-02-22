using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Configuration;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderWithCreatedCacheListAdapterFactory : EventDataGeneratorAdapterFactory
    {
        private readonly ConcurrentBag<QueueCacheForTesting> createdCaches = new ConcurrentBag<QueueCacheForTesting>();

        public EHStreamProviderWithCreatedCacheListAdapterFactory(string name, EventDataGeneratorStreamOptions options, IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, options, serviceProvider, serializationManager, telemetryProducer, loggerFactory)

        {
            this.createdCaches = new ConcurrentBag<QueueCacheForTesting>();
        }

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamOptions options)
        {
            var eventHubPath = options.Path;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new CacheFactoryForTesting(this.Name, options, this.SerializationManager, this.createdCaches, sharedDimensions, this.serviceProvider.GetRequiredService<ILoggerFactory>());
        }

        private class CacheFactoryForTesting : EventHubQueueCacheFactory
        {
            private readonly ConcurrentBag<QueueCacheForTesting> caches; 
            private readonly string name;

            public CacheFactoryForTesting(string name, EventHubStreamOptions options,
                SerializationManager serializationManager, ConcurrentBag<QueueCacheForTesting> caches, EventHubMonitorAggregationDimensions sharedDimensions,
                ILoggerFactory loggerFactory,
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                : base(options, serializationManager, sharedDimensions, loggerFactory, cacheMonitorFactory, blockPoolMonitorFactory)
            {
                this.name = name;
                this.caches = caches;
            }

            private const int DefaultMaxAddCount = 10;
            protected override IEventHubQueueCache CreateCache(string partition, EventHubStreamOptions options, IStreamQueueCheckpointer<string> checkpointer,
                ILoggerFactory loggerFactory, IObjectPool<FixedSizeBuffer> bufferPool, string blockPoolId,  TimePurgePredicate timePurge,
                SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions, ITelemetryProducer telemetryProducer)
            {
                var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
                var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, loggerFactory, telemetryProducer);
                var cacheLogger = loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{this.name}.{partition}");
                //set defaultMaxAddCount to 10 so TryCalculateCachePressureContribution will start to calculate real contribution shortly
                var cache = new QueueCacheForTesting(DefaultMaxAddCount, checkpointer, new EventHubDataAdapter(serializationManager, bufferPool),
                    EventHubDataComparer.Instance, cacheLogger, new EventHubCacheEvictionStrategy(cacheLogger, timePurge, cacheMonitor, options.StatisticMonitorWriteInterval),
                    cacheMonitor, options.StatisticMonitorWriteInterval);
                this.caches.Add(cache);
                return cache;
            }
        }

        private class QueueCacheForTesting : EventHubQueueCache, IQueueFlowController
        {
            public bool IsUnderPressure { get; private set; }

            public QueueCacheForTesting(int defaultMaxAddCount, IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter,
                ICacheDataComparer<CachedEventHubMessage> comparer, ILogger logger, IEvictionStrategy<CachedEventHubMessage> evictionStrategy,
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
                    return Task.FromResult<object>(this.createdCaches.Any(cache => cache.IsUnderPressure));
                default: return base.ExecuteCommand(command, arg);
            }
        }

        public new static EHStreamProviderWithCreatedCacheListAdapterFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<EventDataGeneratorStreamOptions> streamOptionsSnapshot = services.GetRequiredService<IOptionsSnapshot<EventDataGeneratorStreamOptions>>();
            var factory = ActivatorUtilities.CreateInstance<EHStreamProviderWithCreatedCacheListAdapterFactory>(services, name, streamOptionsSnapshot.Get(name));
            factory.Init();
            return factory;
        }
    }
}