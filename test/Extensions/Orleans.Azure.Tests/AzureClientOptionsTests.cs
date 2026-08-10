using Orleans.Configuration;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("BVT")]
public class AzureClientOptionsTests
{
    [Fact]
    public void TableServiceClient_RejectsNull()
    {
        var options = new Orleans.Persistence.AzureStorage.AzureStorageOperationOptions();

        Assert.Throws<ArgumentNullException>(() => options.TableServiceClient = null!);
    }

    [Fact]
    public void BlobStorageServiceClient_RejectsNull()
    {
        var options = new AzureBlobStorageOptions();

        Assert.Throws<ArgumentNullException>(() => options.BlobServiceClient = null!);
    }

    [Fact]
    public void BlobLeaseServiceClient_RejectsNull()
    {
        var options = new AzureBlobLeaseProviderOptions();

        Assert.Throws<ArgumentNullException>(() => options.BlobServiceClient = null!);
    }

    [Fact]
    public void QueueServiceClient_RejectsNull()
    {
        var options = new AzureQueueOptions();

        Assert.Throws<ArgumentNullException>(() => options.QueueServiceClient = null!);
    }
}
