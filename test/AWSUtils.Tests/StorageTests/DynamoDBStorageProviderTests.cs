using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using TestExtensions;
using UnitTests.StorageTests.Relational;
using Xunit;

namespace AWSUtils.Tests.StorageTests
{
    [TestCategory("Persistence"), TestCategory("AWS"), TestCategory("DynamoDb")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class DynamoDBStorageProviderTests
    {
        internal CommonStorageTests PersistenceStorageTests { get; }
        private IProviderRuntime DefaultProviderRuntime { get; }
        private const string TABLE_NAME = "DynamoDBStorageProviderTests";

        public DynamoDBStorageProviderTests(TestEnvironmentFixture fixture)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to DynamoDB simulator");

            DefaultProviderRuntime = new ClientProviderRuntime(fixture.InternalGrainFactory, fixture.Services, NullLoggerFactory.Instance);

            var properties = new Dictionary<string, string>();
            properties["DataConnectionString"] = $"Service={AWSTestConstants.Service}";
            var config = new ProviderConfiguration(properties);
            var provider = new DynamoDBStorageProvider();
            provider.Init("DynamoDBStorageProviderTests", DefaultProviderRuntime, config).Wait();
            PersistenceStorageTests = new CommonStorageTests(fixture.InternalGrainFactory, provider);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task WriteRead100StatesInParallel()
        {
            await PersistenceStorageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel();
        }
    }
}
