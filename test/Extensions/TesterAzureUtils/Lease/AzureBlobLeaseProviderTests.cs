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
            :base(CreateLeaseProvider(), output)
        {
        }

        private static ILeaseProvider CreateLeaseProvider()
        {
            TestUtils.CheckForAzureStorage();
            return new AzureBlobLeaseProvider(new AzureBlobLeaseProviderConfig()
            {
                DataConnectionString = TestDefaultConfiguration.DataConnectionString,
                BlobContainerName = "test-blob-container-name"
            });
        }
    }
}

