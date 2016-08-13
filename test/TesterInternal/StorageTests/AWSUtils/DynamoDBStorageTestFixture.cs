namespace UnitTests.StorageTests.AWSUtils
{
    public class DynamoDBStorageTestsFixture
    {
        internal UnitTestDynamoDBStorage DataManager { get; set; }

        public DynamoDBStorageTestsFixture()
        {
            DataManager = new UnitTestDynamoDBStorage();
        }
    }
}
