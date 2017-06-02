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
        public class AdapterFactory : EHStreamProviderWithCreatedCacheList.AdapterFactory
        {
            public override void Init(IProviderConfiguration providerCfg, string providerName, Logger log, IServiceProvider svcProvider)
            {
                this.ReceiverMonitorFactory = (dimensions, logger) => EventHubReceiverMonitorForTesting.Instance;
                base.Init(providerCfg, providerName, log, svcProvider);
            }

            protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamProviderSettings providerSettings)
            {
                var globalConfig = this.serviceProvider.GetRequiredService<GlobalConfiguration>();
                var nodeConfig = this.serviceProvider.GetRequiredService<NodeConfiguration>();
                var eventHubPath = hubSettings.Path;
                var sharedDimensions = new EventHubMonitorAggregationDimensions(globalConfig, nodeConfig, eventHubPath);
                Func<EventHubCacheMonitorDimensions, Logger, ICacheMonitor> cacheMonitorFactory = (dimensions, logger) => CacheMonitorForTesting.Instance;
                Func<EventHubBlockPoolMonitorDimensions, Logger, IBlockPoolMonitor> blockPoolMonitorFactory = (dimensions, logger) =>BlockPoolMonitorForTesting.Instance;
                return new EHStreamProviderWithCreatedCacheList.AdapterFactory.CacheFactoryForTesting(providerSettings, SerializationManager, this.createdCaches,
                    sharedDimensions, cacheMonitorFactory, blockPoolMonitorFactory);
            }

            public enum QueryCommands
            {
                GetCacheMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 10, 
                GetReceiverMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 11,
                GetObjectPoolMonitorCallCounters = (int)PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 12
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
                    default: return base.ExecuteCommand(command, arg);

                }
                return Task.FromResult(re);
            }
        }
    }
}
