using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Orleans.Storage;
using TestExtensions;
using UnitTests.StorageTests.Relational;
using Xunit;

namespace AWSUtils.Tests.StorageTests
{
    [TestCategory("Persistence"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    internal class DynamoDBStorageProviderTests
    {
        protected CommonStorageTests PersistenceStorageTests { get; }
        private IProviderRuntime DefaultProviderRuntime { get; }
        private const string TABLE_NAME = "DynamoDBStorageProviderTests";

        public DynamoDBStorageProviderTests(TestEnvironmentFixture fixture)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to DynamoDB simulator");

            DefaultProviderRuntime = new StorageProviderManager(
                fixture.GrainFactory,
                null,
                new ClientProviderRuntime(fixture.InternalGrainFactory, null));
            ((StorageProviderManager) DefaultProviderRuntime).LoadEmptyStorageProviders().WaitWithThrow(TestConstants.InitTimeout);

            var properties = new Dictionary<string, string>();
            properties["DataConnectionString"] = $"Service={AWSTestConstants.Service}";
            var config = new ProviderConfiguration(properties, null);
            var provider = new DynamoDBStorageProvider();
            provider.Init("DynamoDBStorageProviderTests", DefaultProviderRuntime, config).Wait();
            PersistenceStorageTests = new CommonStorageTests(fixture.InternalGrainFactory, provider);
        }

        [SkippableFact, TestCategory("Functional")]
        internal async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }

        [SkippableFact, TestCategory("Functional")]
        internal async Task WriteRead100StatesInParallel()
        {
            await PersistenceStorageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel();
        }
    }
}
