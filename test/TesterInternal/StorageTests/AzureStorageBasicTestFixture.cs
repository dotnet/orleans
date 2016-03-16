using Tester;

namespace UnitTests.StorageTests
{
    public class AzureStorageBasicTestFixture
    {
        public AzureStorageBasicTestFixture()
        {
            TestUtils.CheckForAzureStorage();
        }
    }
}
