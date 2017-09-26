using Xunit.Abstractions;
using Orleans.LeaseProviders;
using TestExtensions;
using TestExtensions.Runners;

namespace Tester.AzureUtils.Lease
{
    [TestCategory("Functional"), TestCategory("Azure"), TestCategory("Lease")]
    public class AzureBlobLeaseProviderTests : GoldenPathLeaseProviderTestRunner
    {
        public AzureBlobLeaseProviderTests(ITestOutputHelper output)
            :base(new AzureBlobLeaseProvider(new AzureBlobLeaseProviderConfig()
            {
                DataConnectionString = TestDefaultConfiguration.DataConnectionString,
                BlobContainerName = "test-blob-container-name"
            }), output)
        {
            TestUtils.CheckForAzureStorage();
        }
    }
}

