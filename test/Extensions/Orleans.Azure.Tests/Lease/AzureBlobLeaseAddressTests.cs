using System.Reflection;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.LeaseProviders;
using Xunit;

namespace Tester.AzureUtils.Lease;

[TestCategory("BVT"), TestCategory("AzureStorage"), TestCategory("Lease")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Lease")]
public class AzureBlobLeaseAddressTests
{
    public static IEnumerable<TheoryDataRow<string, string, string>> AddressCases =>
    [
        new("StReAm", "ReSoUrCe42", "https://example.test/leases/stream-resource42.json")
        {
            TestDisplayName = "GetBlobClient_PersistedAddress_MixedCase",
        },
        new("Queue-BALANCER", "Partition-A1", "https://example.test/leases/queue-balancer-partition-a1.json")
        {
            TestDisplayName = "GetBlobClient_PersistedAddress_HyphenatedComponents",
        },
    ];

    [Theory, MemberData(nameof(AddressCases))]
    public void GetBlobClient_PreservesPersistedLowercaseAddress(string category, string resourceKey, string expectedUri)
    {
        var provider = new AzureBlobLeaseProvider(Options.Create(new AzureBlobLeaseProviderOptions()));
        var container = new BlobContainerClient(new Uri("https://example.test/leases"));
        typeof(AzureBlobLeaseProvider).GetField("container", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, container);

        var client = Assert.IsType<BlobClient>(
            typeof(AzureBlobLeaseProvider).GetMethod("GetBlobClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(provider, [category, resourceKey]));

        Assert.Equal(expectedUri, client.Uri.AbsoluteUri);
    }
}
