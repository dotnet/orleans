
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Tester.TestStreamProviders;

namespace Tester.AzureUtils.Streaming
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
