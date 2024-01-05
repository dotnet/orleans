using Amazon.DynamoDBv2.Model;
using Xunit;

namespace AWSUtils.Tests.StorageTests.AWSUtils
{
    [TestCategory("Storage"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class DynamoDBStorageTests : IClassFixture<DynamoDBStorageTestsFixture>
    {
        private readonly string PartitionKey;
        private readonly UnitTestDynamoDBStorage manager;

        public DynamoDBStorageTests(DynamoDBStorageTestsFixture fixture)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");

            manager = fixture.DataManager;
            PartitionKey = "PK-DynamoDBDataManagerTests-" + Guid.NewGuid();
        }

        private UnitTestDynamoDBTableData GenerateNewData()
        {
            return new UnitTestDynamoDBTableData("JustData", PartitionKey, "RK-" + Guid.NewGuid());
        }

        [SkippableFact,  TestCategory("Functional")]
        public async Task DynamoDBDataManager_CreateItemAsync()
        {
            var expression = "attribute_not_exists(PartitionKey) AND attribute_not_exists(RowKey)";
            var toPersist = GenerateNewData();
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true), expression);
            var originalEtag = toPersist.ETag;
            var persisted = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), response => new UnitTestDynamoDBTableData(response) );
            Assert.Equal(toPersist.StringData, persisted.StringData);
            Assert.True(persisted.ETag == 0);
            Assert.Equal(originalEtag, persisted.ETag);

            await Assert.ThrowsAsync<ConditionalCheckFailedException>(async () =>
            {
                var toPersist2 = toPersist.Clone();
                await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true), expression);
            });
        }

        [SkippableFact,  TestCategory("Functional")]
        public async Task DynamoDBDataManager_UpsertItemAsync()
        {
            var expression = "attribute_not_exists(PartitionKey) AND attribute_not_exists(RowKey)";
            var toPersist = GenerateNewData();
            toPersist.StringData = "Create";
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true), expression);

            toPersist.StringData = "Replaced";            
            await manager.UpsertEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), GetValues(toPersist));
            var persisted = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), response => new UnitTestDynamoDBTableData(response));
            Assert.Equal("Replaced", persisted.StringData);
            Assert.True(persisted.ETag == 0); //Yes, ETag didn't changed cause we didn't 

            persisted.StringData = "Updated";
            var persistedEtag = persisted.ETag;
            expression = $"ETag = :OldETag";
            persisted.ETag++; //Increase ETag
            var expValues = new Dictionary<string, AttributeValue> { { ":OldETag", new AttributeValue { N = persistedEtag.ToString() } } };
            await manager.UpsertEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(persisted), GetValues(persisted), expression, expValues);
            persisted = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), response => new UnitTestDynamoDBTableData(response));
            Assert.Equal("Updated", persisted.StringData);
            Assert.NotEqual(persistedEtag, persisted.ETag); //Now ETag changed cause we did it

            await Assert.ThrowsAsync<ConditionalCheckFailedException>(async () =>
            {
                await manager.UpsertEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), GetValues(toPersist), expression, expValues);
            });
        }

        [SkippableFact,  TestCategory("Functional")]
        public async Task DynamoDBDataManager_DeleteItemAsync()
        {
            var toPersist = GenerateNewData();
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true));
            await manager.DeleteEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist));
            var persisted = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), response => new UnitTestDynamoDBTableData(response));
            Assert.Null(persisted);
        }

        [SkippableFact,  TestCategory("Functional")]
        public async Task DynamoDBDataManager_ReadSingleTableEntryAsync()
        {
            var toPersist = GenerateNewData();
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true));
            var persisted = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(toPersist), response => new UnitTestDynamoDBTableData(response));
            Assert.NotNull(persisted);

            var data = GenerateNewData();
            var notFound = await manager.ReadSingleEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetKeys(data), response => new UnitTestDynamoDBTableData(response));
            Assert.Null(notFound);
        }

        [SkippableFact,  TestCategory("Functional")]
        public async Task DynamoDBDataManager_ReadAllTableEntryByPartitionAsync()
        {
            var toPersist = GenerateNewData();
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist, true));
            var toPersist2 = toPersist.Clone();
            toPersist2.RowKey += "otherKey";
            await manager.PutEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, GetValues(toPersist2, true));
            var keys = new Dictionary<string, AttributeValue> { { ":PK", new AttributeValue(toPersist.PartitionKey) } };
            var found = await manager.QueryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, keys, $"PartitionKey = :PK", item => new UnitTestDynamoDBTableData(item));
            Assert.NotNull(found.results);
            Assert.True(found.results.Count == 2);
        }
        
        internal static Dictionary<string, AttributeValue> GetKeys(UnitTestDynamoDBTableData data)
        {
            var keys = new Dictionary<string, AttributeValue>();
            keys.Add("PartitionKey", new AttributeValue(data.PartitionKey));
            keys.Add("RowKey", new AttributeValue(data.RowKey));
            return keys;
        }

        internal static Dictionary<string, AttributeValue> GetValues(UnitTestDynamoDBTableData data, bool includeKeys = false)
        {
            var values = new Dictionary<string, AttributeValue>();
            if (!string.IsNullOrWhiteSpace(data.StringData))
            {
                values.Add("StringData", new AttributeValue(data.StringData)); 
            }
            if (data.BinaryData != null && data.BinaryData.Length > 0)
            {
                values.Add("BinaryData", new AttributeValue { B = new MemoryStream(data.BinaryData) }); 
            }

            if (includeKeys)
            {
                values.Add("PartitionKey", new AttributeValue(data.PartitionKey));
                values.Add("RowKey", new AttributeValue(data.RowKey));
            }

            values.Add("ETag", new AttributeValue { N = data.ETag.ToString() });
            return values;
        }
    }
}
