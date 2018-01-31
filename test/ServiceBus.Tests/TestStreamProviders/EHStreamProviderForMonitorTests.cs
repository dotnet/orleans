using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.ServiceBus.Providers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ServiceBus.Providers.Testing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Runtime.Configuration;
using System.Threading;
using Microsoft.Azure.EventHubs;
using Microsoft.Extensions.Logging;
using ServiceBus.Tests.MonitorTests;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderForMonitorTests : PersistentStreamProvider<EHStreamProviderForMonitorTests.AdapterFactory>
    {
        public class AdapterFactory : EventDataGeneratorStreamProvider.AdapterFactory
        {
            private CachePressureInjectionMonitor cachePressureInjectionMonitor;

            public override void Init(IProviderConfiguration providerCfg, string providerName,  IServiceProvider svcProvider)
            {
                this.ReceiverMonitorFactory = (dimensions, logger, telemetryProducer) => EventHubReceiverMonitorForTesting.Instance;
                this.cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
                base.Init(providerCfg, providerName, svcProvider);
            }

            private void ChangeCachePressure()
            {
                this.cachePressureInjectionMonitor.UnderPressure = !this.cachePressureInjectionMonitor.UnderPressure;
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var loggerFactory = this.serviceProvider.GetRequiredService<ILoggerFactory>();
                var eventHubPath = this.hubSettings.Path;
                var sharedDimensions = new EventHubMonitorAggregationDimensions(eventHubPath);
                Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = (dimensions, logger, telemetryProducer) => CacheMonitorForTesting.Instance;
                Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = (dimensions, logger, telemetryProducer) =>BlockPoolMonitorForTesting.Instance;
                return new CacheFactoryForMonitorTesting(this.cachePressureInjectionMonitor, providerSettings, this.SerializationManager,
                    sharedDimensions, loggerFactory, cacheMonitorFactory, blockPoolMonitorFactory);
            }

            private class CacheFactoryForMonitorTesting : EventHubQueueCacheFactory
            {
                private CachePressureInjectionMonitor cachePressureInjectionMonitor;
                public CacheFactoryForMonitorTesting(CachePressureInjectionMonitor cachePressureInjectionMonitor, EventHubStreamProviderSettings providerSettings,
                   SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
                   ILoggerFactory loggerFactory,
                   Func<EventHubCacheMonitorDimensions, ILoggerFactory, ITelemetryProducer, ICacheMonitor> cacheMonitorFactory = null,
                   Func<EventHubBlockPoolMonitorDimensions, ILoggerFactory, ITelemetryProducer, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                    : base(providerSettings, serializationManager, sharedDimensions, loggerFactory, cacheMonitorFactory, blockPoolMonitorFactory)
                {
                    this.cachePressureInjectionMonitor = cachePressureInjectionMonitor;
                }

                protected override void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamProviderSettings providerSettings,
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
