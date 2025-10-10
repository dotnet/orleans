using System;
using Azure.Storage.Blobs;

namespace Orleans.ScheduledJobs.AzureStorage;

public class AzureStorageJobShardOptions
{
    /// <summary>
    /// The maximum duration of a job shard.
    /// </summary>
    public TimeSpan MaxShardDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the <see cref="BlobServiceClient"/> instance used to store job shards.
    /// </summary>
    public BlobServiceClient BlobServiceClient { get; set; } = null!;

    /// <summary>
    /// Gets or sets the name of the container used to store scheduled jobs.
    /// </summary>
    public string ContainerName { get; set; } = "scheduled-jobs";
}
