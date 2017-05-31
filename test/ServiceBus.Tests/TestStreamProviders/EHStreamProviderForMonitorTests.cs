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
                this.ReceiverMonitorFactory = (dimentions, logger) => EventHubReceiverMonitorForTesting.Instance;
                this.CacheMonitorFactory = (dimentions, logger) => CacheMonitorForTesting.Instance;
                this.ObjectPoolMonitorFactory = (dimentions, logger) =>ObjectPoolMonitorForTesting.Instance;
                base.Init(providerCfg, providerName, log, svcProvider);
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
                        re = ObjectPoolMonitorForTesting.Instance.CallCounters;
                        break;
                    default: return base.ExecuteCommand(command, arg);

                }
                return Task.FromResult(re);
            }
        }
    }
}
