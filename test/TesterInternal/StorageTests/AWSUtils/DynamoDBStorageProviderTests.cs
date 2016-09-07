using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Orleans.Serialization;
using Orleans.Storage;
using UnitTests.StorageTests.Relational;
using Xunit;

namespace UnitTests.StorageTests.AWSUtils
{

    [TestCategory("Persistence"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class DynamoDBStorageProviderTests
    {
        protected CommonStorageTests PersistenceStorageTests { get; }
        private IProviderRuntime DefaultProviderRuntime { get; }
        private const string TABLE_NAME = "DynamoDBStorageProviderTests";

        public DynamoDBStorageProviderTests()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to DynamoDB simulator");

            DefaultProviderRuntime = new StorageProviderManager(new GrainFactory(), null);
            ((StorageProviderManager)DefaultProviderRuntime).LoadEmptyStorageProviders(new ClientProviderRuntime(new GrainFactory(), null)).WaitWithThrow(TestConstants.InitTimeout);
            SerializationManager.InitializeForTesting();

            var properties = new Dictionary<string, string>();
            properties["DataConnectionString"] = $"Service={AWSTestConstants.Service}";
            var config = new ProviderConfiguration(properties, null);
            var provider = new DynamoDBStorageProvider();
            provider.Init("DynamoDBStorageProviderTests", DefaultProviderRuntime, config).Wait();
            PersistenceStorageTests = new CommonStorageTests(provider);
        }
        
        [SkippableFact, TestCategory("Functional")]
        internal async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }

        [SkippableFact, TestCategory("Functional")]
        internal async Task WriteRead100StatesInParallel()
        {
            await PersistenceStorageTests.PersistenceStorage_WriteReadWriteRead100StatesInParallel();
        }
    }
}
