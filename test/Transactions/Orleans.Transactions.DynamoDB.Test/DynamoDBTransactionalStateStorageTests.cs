using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.AWSUtils.Tests;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB.TransactionalState;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    public class TestState : IEquatable<TestState>
    {
        public int State { get; set; }

        public bool Equals(TestState other)
        {
            return other == null?false:this.State.Equals(other.State);
        }
    }

    /// <summary>
    /// Tests for DynamoDB implementation of transactional state storage.
    /// </summary>
    public class DynamoDBTransactionalStateStorageTests : TransactionalStateStorageTestRunnerxUnit<TestState>, IClassFixture<TestFixture>
    {
        private const string tableName = "StateStorageTests";
        private const string partition = "testpartition";
        public DynamoDBTransactionalStateStorageTests(TestFixture fixture, ITestOutputHelper testOutput)
            : base(() => StateStorageFactory(fixture), (seed) => new TestState() { State = seed }, fixture.GrainFactory, testOutput)
        {
        }

        private static async Task<ITransactionalStateStorage<TestState>> StateStorageFactory(TestFixture fixture)
        {
            var storage = await InitTableAsync(NullLogger.Instance);
            var orleansJsonSerializer = new OrleansJsonSerializer(
                new OptionsWrapper<OrleansJsonSerializerOptions>(new OrleansJsonSerializerOptions()));

            var options = new DynamoDBTransactionalStorageOptions
            {
                TableName = tableName,
                GrainStorageSerializer = new JsonGrainStorageSerializer(orleansJsonSerializer)
            };

            var stateStorage = new DynamoDBTransactionalStateStorage<TestState>(
                storage,
                options,
                $"{partition}{DateTime.UtcNow.Ticks}",
                NullLoggerFactory.Instance.CreateLogger<DynamoDBTransactionalStateStorage<TestState>>());
            return stateStorage;
        }

        private static async Task<DynamoDBStorage> InitTableAsync(ILogger logger)
        {
            try
            {
                var storage = GetDynamoDBStorage(logger);
                await storage.InitializeTable(tableName,
                    new List<KeySchemaElement>
                    {
                        new KeySchemaElement { AttributeName = DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, KeyType = KeyType.HASH },
                        new KeySchemaElement { AttributeName = DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE }
                    },
                    new List<AttributeDefinition>
                    {
                        new AttributeDefinition { AttributeName = DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                        new AttributeDefinition { AttributeName = DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                    },
                    secondaryIndexes: null,
                    null);
                return storage;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Could not initialize connection to storage table {TableName}", tableName);
                throw;
            }
        }

        private static DynamoDBStorage GetDynamoDBStorage(ILogger logger)
        {
            try
            {
                var storage = new DynamoDBStorage(
                    logger,
                    service: AWSTestConstants.DynamoDbService,
                    accessKey: AWSTestConstants.DynamoDbAccessKey,
                    secretKey: AWSTestConstants.DynamoDbSecretKey);
                return storage;
            }
            catch (Exception exc)
            {
                logger.LogError(exc, "Error creating CloudTableCreationClient");
                throw;
            }
        }
    }
}
