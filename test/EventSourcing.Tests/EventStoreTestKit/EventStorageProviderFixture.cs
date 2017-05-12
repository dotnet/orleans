using Orleans;
using Orleans.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Storage;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Xunit;
using Orleans.Runtime;
using Orleans.EventSourcing;

namespace EventSourcing.Tests
{
    /// <summary>
    /// A test fixture providing a manager that can initialize event storage providers
    /// </summary>
    public class EventStorageProviderFixture
    {

        public EventStorageProviderFixture(TestEnvironmentFixture fixture) 
        {
            manager = new EventStorageProviderManager(fixture.GrainFactory, fixture.Services,
                 new ClientProviderRuntime(fixture.InternalGrainFactory, fixture.Services));

            manager.LoadEmptyProviders().WaitWithThrow(TestConstants.InitTimeout);
        }

        private readonly EventStorageProviderManager manager;

        /// <summary> 
        /// initialize a provider
        /// </summary>
        public void InitProvider(IEventStorageProvider provider, string name, Dictionary<string, string> providerConfigProps = null)
        {
            var cfg = new ProviderConfiguration(providerConfigProps, null);
            provider.Init(name, manager, cfg).WaitWithThrow(TestConstants.InitTimeout);
        }
    }

}
