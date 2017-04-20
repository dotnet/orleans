
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Tester.TestStreamProviders;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestEventHubStreamProvider : PersistentStreamProvider<TestEventHubStreamProvider.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory
        {
            public AdapterFactory()
            {
                StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create(this.serviceProvider.GetRequiredService<SerializationManager>());
            }
        }
    }
}
