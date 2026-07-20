namespace AWSUtils.Tests.StorageTests
{
    public class DynamoDBStorageTestsFixture
    {
        internal UnitTestDynamoDBStorage DataManager { get; set; } = null!;

        public DynamoDBStorageTestsFixture()
        {
            if (AWSTestConstants.IsDynamoDbAvailable)
            {
                DataManager = new UnitTestDynamoDBStorage();
            }
        }
    }
}
