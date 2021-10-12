using System;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Serialization;
using Orleans.Configuration;
using ServiceBus.Tests.MonitorTests;
using Orleans;

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
            ITelemetryProducer telemetryProducer,
            ILoggerFactory loggerFactory)
            : base(name, options, ehOptions, receiverOptions, cacheOptions, streamCacheEvictionOptions, statisticOptions, dataAdapter, serviceProvider, telemetryProducer, loggerFactory)
        {
            this.cacheOptions = cacheOptions;
            this.staticticOptions = statisticOptions;
            this.ehOptions = ehOptions;
            this.evictionOptions = streamCacheEvictionOptions;
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
            this.ReceiverMonitorFactory = (dimensions, logger, telemetryProducer) => eventHubReceiverMonitorForTesting;
            this.cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
            base.Init();
        }

        private void ChangeCachePressure()
        {
            this.cachePressureInjectionMonitor.UnderPressure = !this.cachePressureInjectionMonitor.UnderPressure;
        }

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions cacheOptions)
        {
            var loggerFactory = this.serviceProvider.GetRequiredService<ILoggerFactory>();
            var eventHubPath = this.ehOptions.EventHubName;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = (dimensions, logger, telemetryProducer) => this.cacheMonitorForTesting;
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = (dimensions, logger, telemetryProducer) => this.blockPoolMonitorForTesting;
            return new CacheFactoryForMonitorTesting(
                this.cachePressureInjectionMonitor,
                this.cacheOptions,
                this.evictionOptions,
                this.staticticOptions,
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
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                : base(cacheOptions, streamCacheEviction, statisticOptions, dataAdater, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
            {
                this.cachePressureInjectionMonitor = cachePressureInjectionMonitor;
            }

            protected override void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamCachePressureOptions providerOptions,
                    ILogger cacheLogger)
            {
                cache.AddCachePressureMonitor(this.cachePressureInjectionMonitor);
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
                    re = this.cacheMonitorForTesting.CallCounters;
                    break;
                case (int)QueryCommands.GetReceiverMonitorCallCounters:
                    re = this.eventHubReceiverMonitorForTesting.CallCounters;
                    break;
                case (int)QueryCommands.GetObjectPoolMonitorCallCounters:
                    re = this.blockPoolMonitorForTesting.CallCounters;
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
            this.UnderPressure = false;
            this.wasUnderPressur = this.UnderPressure;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {

        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            if (this.wasUnderPressur != this.UnderPressure)
            {
                this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, this.UnderPressure, null, null, null);
                this.wasUnderPressur = this.UnderPressure;
            }
            return this.UnderPressure;
        }
    }
}
