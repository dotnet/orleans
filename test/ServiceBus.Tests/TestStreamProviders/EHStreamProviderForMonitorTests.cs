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
#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
#endif
using ServiceBus.Tests.MonitorTests;

namespace ServiceBus.Tests.TestStreamProviders
{
    public class EHStreamProviderForMonitorTests : PersistentStreamProvider<EHStreamProviderForMonitorTests.AdapterFactory>
    {
        public class AdapterFactory : EventDataGeneratorStreamProvider.AdapterFactory
        {
            private CachePressureInjectionMonitor cachePressureInjectionMonitor;
            
            public override void Init(IProviderConfiguration providerCfg, string providerName, Logger log, IServiceProvider svcProvider)
            {
                this.ReceiverMonitorFactory = (dimensions, logger) => EventHubReceiverMonitorForTesting.Instance;
                this.cachePressureInjectionMonitor = new CachePressureInjectionMonitor();
                base.Init(providerCfg, providerName, log, svcProvider);
            }

            private void ChangeCachePressure()
            {
                cachePressureInjectionMonitor.isUnderPressure = !cachePressureInjectionMonitor.isUnderPressure;
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                var nodeConfig = this.serviceProvider.GetRequiredService<NodeConfiguration>();
                var eventHubPath = hubSettings.Path;
                var sharedDimensions = new EventHubMonitorAggregationDimensions(globalConfig, nodeConfig, eventHubPath);
                Func<EventHubCacheMonitorDimensions, Logger, ICacheMonitor> cacheMonitorFactory = (dimensions, logger) => CacheMonitorForTesting.Instance;
                Func<EventHubBlockPoolMonitorDimensions, Logger, IBlockPoolMonitor> blockPoolMonitorFactory = (dimensions, logger) =>BlockPoolMonitorForTesting.Instance;
                return new CacheFactoryForMonitorTesting(this.cachePressureInjectionMonitor, providerSettings, SerializationManager,
                    sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory);
            }

            private class CacheFactoryForMonitorTesting : EventHubQueueCacheFactory
            {
                private CachePressureInjectionMonitor cachePressureInjectionMonitor;
                public CacheFactoryForMonitorTesting(CachePressureInjectionMonitor cachePressureInjectionMonitor, EventHubStreamProviderSettings providerSettings,
                   SerializationManager serializationManager, EventHubMonitorAggregationDimensions sharedDimensions,
                   Func<EventHubCacheMonitorDimensions, Logger, ICacheMonitor> cacheMonitorFactory = null,
                   Func<EventHubBlockPoolMonitorDimensions, Logger, IBlockPoolMonitor> blockPoolMonitorFactory = null)
                    : base(providerSettings, serializationManager, sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory)
                {
                    this.cachePressureInjectionMonitor = cachePressureInjectionMonitor;
                }

                protected override void AddCachePressureMonitors(IEventHubQueueCache cache, EventHubStreamProviderSettings providerSettings,
                        Logger cacheLogger)
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
        public bool isUnderPressure { get; set; }
        private bool wasUnderPressur;
        public ICacheMonitor CacheMonitor { set; private get; }
        public CachePressureInjectionMonitor()
        {
            this.isUnderPressure = false;
            this.wasUnderPressur = this.isUnderPressure;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {

        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            if (this.wasUnderPressur != this.isUnderPressure)
            {
                this.CacheMonitor?.TrackCachePressureMonitorStatusChange(this.GetType().Name, this.isUnderPressure, null, null, null);
                this.wasUnderPressur = this.isUnderPressure;
            }
            return this.isUnderPressure;
        }
    }
}
