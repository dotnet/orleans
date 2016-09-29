
using Orleans.Providers.Streams.Common;
using Orleans.ServiceBus.Providers;

namespace Tester.TestStreamProviders.EventHub
{
    public class TestEventHubStreamProvider : PersistentStreamProvider<TestEventHubStreamProvider.AdapterFactory>
    {
        public class AdapterFactory : EventHubAdapterFactory
        {
            public AdapterFactory()
            {
                StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create();
            }
        }
    }
}
