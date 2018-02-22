using System;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Serialization;
using Orleans.Configuration;
using ServiceBus.Tests.MonitorTests;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderForMonitorTestsAdapterFactory : EventDataGeneratorAdapterFactory
    {
        private CachePressureInjectionMonitor cachePressureInjectionMonitor;

        public EHStreamProviderForMonitorTestsAdapterFactory(string name, EventDataGeneratorStreamOptions options, IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, options, serviceProvider, serializationManager, telemetryProducer, loggerFactory)
        {
        }

        public override void Init()
        {
            this.ReceiverMonitorFactory = (dimensions, logger, telemetryProducer) => EventHubReceiverMonitorForTesting.Instance;
            this.cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
            base.Init();
        }

        private void ChangeCachePressure()
        {
            this.cachePressureInjectionMonitor.UnderPressure = !this.cachePressureInjectionMonitor.UnderPressure;
        }

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamOptions options)
        {
            var loggerFactory = this.serviceProvider.GetRequiredService<ILoggerFactory>();
            var eventHubPath = options.Path;
            var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
            Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = (dimensions, logger, telemetryProducer) => CacheMonitorForTesting.Instance;
            Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = (dimensions, logger, telemetryProducer) =>BlockPoolMonitorForTesting.Instance;
            return new CacheFactoryForMonitorTesting(this.cachePressureInjectionMonitor, options, this.SerializationManager,
                sharedDimensions, loggerFactory, cacheMonitorFactory, blockPoolMonitorFactory);
        }

        private class CacheFactoryForMonitorTesting : EventHubQueueCacheFactory
        {
            private CachePressureInjectionMonitor cachePressureInjectionMonitor;
            public CacheFactoryForMonitorTesting(CachePressureInjectionMonitor cachePressureInjectionMonitor, EventHubStreamOptions options,
                SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
                ILoggerFactory loggerFactory,
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                : base(options, serializationManager, sharedDimensions, loggerFactory, cacheMonitorFactory, blockPoolMonitorFactory)
            {
                this.cachePressureInjectionMonitor = cachePressureInjectionMonitor;
            }

            protected override void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamOptions options,
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
                    re = CacheMonitorForTesting.Instance.CallCounters;
                    break;
                case (int)QueryCommands.GetReceiverMonitorCallCounters:
                    re = EventHubReceiverMonitorForTesting.Instance.CallCounters;
                    break;
                case (int)QueryCommands.GetObjectPoolMonitorCallCounters:
                    re = BlockPoolMonitorForTesting.Instance.CallCounters;
                    break;
                case (int)QueryCommands.ChangeCachePressure:
                    ChangeCachePressure();
                    break;
                default: return base.ExecuteCommand(command, arg);

            }
            return Task.FromResult(re);
        }

        public new static EHStreamProviderForMonitorTestsAdapterFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<EventDataGeneratorStreamOptions> streamOptionsSnapshot = services.GetRequiredService<IOptionsSnapshot<EventDataGeneratorStreamOptions>>();
            var factory = ActivatorUtilities.CreateInstance<EHStreamProviderForMonitorTestsAdapterFactory>(services, name, streamOptionsSnapshot.Get(name));
            factory.Init();
            return factory;
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
