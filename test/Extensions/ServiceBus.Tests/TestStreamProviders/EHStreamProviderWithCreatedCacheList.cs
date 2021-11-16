using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Configuration;
using Orleans;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderWithCreatedCacheListAdapterFactory : EventDataGeneratorAdapterFactory
    {
        private readonly ConcurrentBag<QueueCacheForTesting> createdCaches = new ConcurrentBag<QueueCacheForTesting>();
        private readonly EventHubStreamCachePressureOptions cacheOptions;
        private readonly StreamStatisticOptions staticticOptions;
        private readonly EventHubOptions ehOptions;
        private readonly StreamCacheEvictionOptions evictionOptions;
        public EHStreamProviderWithCreatedCacheListAdapterFactory(
            string name,
            EventDataGeneratorStreamOptions options,
            EventHubOptions ehOptions,
            EventHubReceiverOptions receiverOptions,
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions evictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdatper,
            IServiceProvider serviceProvider, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, options, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdatper, serviceProvider, telemetryProducer, loggerFactory)

        {
            this.createdCaches = new ConcurrentBag<QueueCacheForTesting>();
            this.cacheOptions = cacheOptions;
            this.staticticOptions = statisticOptions;
            this.ehOptions = ehOptions;
            this.evictionOptions = evictionOptions;
        }

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions options)
        {
            var eventHubPath = this.ehOptions.EventHubName;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            return new CacheFactoryForTesting(this.Name, this.cacheOptions, this.evictionOptions,this.staticticOptions, base.dataAdapter, this.createdCaches, sharedDimensions, this.serviceProvider.GetRequiredService<ILoggerFactory>());
        }

        private class CacheFactoryForTesting : EventHubQueueCacheFactory
        {
            private readonly ConcurrentBag<QueueCacheForTesting> caches; 
            private readonly string name;

            public CacheFactoryForTesting(string name, EventHubStreamCachePressureOptions cacheOptions, StreamCacheEvictionOptions evictionOptions, StreamStatisticOptions statisticOptions,
                IEventHubDataAdapter dataAdapter, ConcurrentBag<QueueCacheForTesting> caches, EventHubMonitorAggregationDimensions sharedDimensions,
                ILoggerFactory loggerFactory,
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                : base(cacheOptions, evictionOptions, statisticOptions, dataAdapter, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
            {
                this.name = name;
                this.caches = caches;
            }
            private const int DefaultMaxAddCount = 10;
            protected override IEventHubQueueCache CreateCache(
                string partition,
                IEventHubDataAdapter dataAdatper,
                StreamStatisticOptions options,
                StreamCacheEvictionOptions evictionOptions,
                IStreamQueueCheckpointer<string> checkpointer,
                ILoggerFactory loggerFactory,
                IObjectPool<FixedSizeBuffer> bufferPool,
                string blockPoolId,
                TimePurgePredicate timePurge,
                EventHubMonitorAggregationDimensions sharedDimensions,
                ITelemetryProducer telemetryProducer)
            {
                var cacheMonitorDimensions = new EventHubCacheMonitorDimensions(sharedDimensions, partition, blockPoolId);
                var cacheMonitor = this.CacheMonitorFactory(cacheMonitorDimensions, loggerFactory, telemetryProducer);
                var cacheLogger = loggerFactory.CreateLogger($"{typeof(EventHubQueueCache).FullName}.{this.name}.{partition}");
                var evictionStrategy = new ChronologicalEvictionStrategy(cacheLogger, timePurge, cacheMonitor, options.StatisticMonitorWriteInterval);
                //set defaultMaxAddCount to 10 so TryCalculateCachePressureContribution will start to calculate real contribution shortly
                var cache = new QueueCacheForTesting(DefaultMaxAddCount, bufferPool, dataAdatper, evictionStrategy, checkpointer,
                    cacheLogger, cacheMonitor, options.StatisticMonitorWriteInterval);
                this.caches.Add(cache);
                return cache;
            }
        }

        private class QueueCacheForTesting : EventHubQueueCache, IQueueFlowController
        {
            public bool IsUnderPressure { get; private set; }

            public QueueCacheForTesting(int defaultMaxAddCount, IObjectPool<FixedSizeBuffer> bufferPool, IEventHubDataAdapter dataAdapter, IEvictionStrategy evictionStrategy, IStreamQueueCheckpointer<string> checkpointer, ILogger logger,
                ICacheMonitor cacheMonitor, TimeSpan? cacheMonitorWriteInterval)
                : base("test", defaultMaxAddCount, bufferPool, dataAdapter, evictionStrategy, checkpointer, logger, cacheMonitor, cacheMonitorWriteInterval, null)
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
            var generatorOptions = services.GetOptionsByName<EventDataGeneratorStreamOptions>(name);
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetServiceByName<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<EHStreamProviderWithCreatedCacheListAdapterFactory>(services, name, generatorOptions, ehOptions, receiverOptions, 
                cacheOptions, evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}