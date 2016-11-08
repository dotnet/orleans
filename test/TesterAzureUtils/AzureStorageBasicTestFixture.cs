using Tester;

namespace Tester.AzureUtils
{
    public class AzureStorageBasicTestFixture
    {
        public AzureStorageBasicTestFixture()
        {
            TestUtils.CheckForAzureStorage();
        }
    }
}
