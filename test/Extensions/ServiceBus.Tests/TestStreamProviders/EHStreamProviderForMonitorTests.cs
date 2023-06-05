using Orleans.Providers.Streams.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Configuration;
using ServiceBus.Tests.MonitorTests;
using Orleans.Statistics;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderForMonitorTestsAdapterFactory : EventDataGeneratorAdapterFactory
    {
        private CachePressureInjectionMonitor cachePressureInjectionMonitor;
        private readonly EventHubStreamCachePressureOptions cacheOptions;
        private readonly StreamCacheEvictionOptions evictionOptions;
        private readonly StreamStatisticOptions staticticOptions;
        private readonly EventHubOptions ehOptions;
        private readonly CacheMonitorForTesting cacheMonitorForTesting = new CacheMonitorForTesting();
        private readonly EventHubReceiverMonitorForTesting eventHubReceiverMonitorForTesting = new EventHubReceiverMonitorForTesting();
        private readonly BlockPoolMonitorForTesting blockPoolMonitorForTesting = new BlockPoolMonitorForTesting();


        public EHStreamProviderForMonitorTestsAdapterFactory(
            string name,
            EventDataGeneratorStreamOptions options,
            EventHubOptions ehOptions,
            EventHubReceiverOptions receiverOptions,
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions streamCacheEvictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdapter,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IHostEnvironmentStatistics hostEnvironmentStatistics)
            : base(name, options, ehOptions, receiverOptions, cacheOptions, streamCacheEvictionOptions, statisticOptions, dataAdapter, serviceProvider, loggerFactory, hostEnvironmentStatistics)
        {
            this.cacheOptions = cacheOptions;
            staticticOptions = statisticOptions;
            this.ehOptions = ehOptions;
            evictionOptions = streamCacheEvictionOptions;
        }

        public new static EHStreamProviderForMonitorTestsAdapterFactory Create(IServiceProvider services, string name)
        {
            var generatorOptions = services.GetOptionsByName<EventDataGeneratorStreamOptions>(name);
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetServiceByName<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<EHStreamProviderForMonitorTestsAdapterFactory>(services, name, generatorOptions, ehOptions, receiverOptions, cacheOptions, 
                evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }

        public override void Init()
        {
            ReceiverMonitorFactory = (dimensions, logger) => eventHubReceiverMonitorForTesting;
            cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
            base.Init();
        }

        private void ChangeCachePressure()
        {
            cachePressureInjectionMonitor.UnderPressure = !cachePressureInjectionMonitor.UnderPressure;
        }

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions cacheOptions)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var eventHubPath = ehOptions.EventHubName;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            ICacheMonitor cacheMonitorFactory(EventHubCacheMonitorDimensions dimensions, ILoggerFactory logger) => cacheMonitorForTesting;
            IBlockPoolMonitor blockPoolMonitorFactory(EventHubBlockPoolMonitorDimensions dimensions, ILoggerFactory logger) => blockPoolMonitorForTesting;
            return new CacheFactoryForMonitorTesting(
                cachePressureInjectionMonitor,
                this.cacheOptions,
                evictionOptions,
                staticticOptions,
                base.dataAdapter,
                sharedDimensions,
                loggerFactory,
                cacheMonitorFactory,
                blockPoolMonitorFactory);
        }

        private class CacheFactoryForMonitorTesting : EventHubQueueCacheFactory
        {
            private CachePressureInjectionMonitor cachePressureInjectionMonitor;
            public CacheFactoryForMonitorTesting(
                CachePressureInjectionMonitor cachePressureInjectionMonitor,
                EventHubStreamCachePressureOptions cacheOptions,
                StreamCacheEvictionOptions streamCacheEviction,
                StreamStatisticOptions statisticOptions,
                IEventHubDataAdapter dataAdater,
                EventHubMonitorAggregationDimensions sharedDimensions,
                ILoggerFactory loggerFactory,
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ICacheMonitor> cacheMonitorFactory = null,
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                : base(cacheOptions, streamCacheEviction, statisticOptions, dataAdater, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
            {
                this.cachePressureInjectionMonitor = cachePressureInjectionMonitor;
            }

            protected override void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamCachePressureOptions providerOptions,
                    ILogger cacheLogger)
            {
                cache.AddCachePressureMonitor(cachePressureInjectionMonitor);
            }
        }
        public enum QueryCommands
        {
            GetCacheMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 10,
            GetReceiverMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 11,
            GetObjectPoolMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 12,
            ChangeCachePressure = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 13
        }

        public override Task<object> ExecuteCommand(int command, object arg)
        {
            object re = null;
            switch (command)
            {
                case (int)QueryCommands.GetCacheMonitorCallCounters:
                    re = cacheMonitorForTesting.CallCounters;
                    break;
                case (int)QueryCommands.GetReceiverMonitorCallCounters:
                    re = eventHubReceiverMonitorForTesting.CallCounters;
                    break;
                case (int)QueryCommands.GetObjectPoolMonitorCallCounters:
                    re = blockPoolMonitorForTesting.CallCounters;
                    break;
                case (int)QueryCommands.ChangeCachePressure:
                    ChangeCachePressure();
                    break;
                default: return base.ExecuteCommand(command, arg);

            }
            return Task.FromResult(re);
        }
    }

    public class CachePressureInjectionMonitor : ICachePressureMonitor
    {
        public bool UnderPressure { get; set; }
        private bool wasUnderPressur;
        public ICacheMonitor CacheMonitor { set; private get; }
        public CachePressureInjectionMonitor()
        {
            UnderPressure = false;
            wasUnderPressur = UnderPressure;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {

        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            if (wasUnderPressur != UnderPressure)
            {
                CacheMonitor?.TrackCachePressureMonitorStatusChange(GetType().Name, UnderPressure, null, null, null);
                wasUnderPressur = UnderPressure;
            }
            return UnderPressure;
        }
    }
}
