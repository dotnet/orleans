
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;

namespace Tester.TestStreamProviders.AzureQueue
{
    public class TestAzureQueueStreamProvider : PersistentStreamProvider<TestAzureQueueStreamProvider.AdapterFactory>
    {
        public class AdapterFactory : AzureQueueAdapterFactory
        {
            public AdapterFactory()
            {
                StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create();
            }
        }
    }
}
