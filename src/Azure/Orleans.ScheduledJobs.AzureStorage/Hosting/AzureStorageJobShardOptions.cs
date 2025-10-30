using Azure.Storage.Blobs;

namespace Orleans.Hosting;

public class AzureStorageJobShardOptions
{
    /// <summary>
    /// Gets or sets the <see cref="BlobServiceClient"/> instance used to store job shards.
    /// </summary>
    public BlobServiceClient BlobServiceClient { get; set; } = null!;

    /// <summary>
    /// Gets or sets the name of the container used to store scheduled jobs.
    /// </summary>
    public string ContainerName { get; set; } = "jobs";
}
