using Xunit.Abstractions;
using Orleans.LeaseProviders;
using TestExtensions.Runners;
using Orleans.Configuration;
using Microsoft.Extensions.Options;

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
            return new AzureBlobLeaseProvider(Options.Create(new AzureBlobLeaseProviderOptions()
            {
                BlobContainerName = "test-blob-container-name"
            }.ConfigureTestDefaults()));
        }
    }
}

