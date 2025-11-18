using System;
using Azure.Storage.Blobs;

namespace Orleans.Hosting;

public class AzureStorageJobShardOptions
{
    /// <summary>
    /// Gets or sets the <see cref="BlobServiceClient"/> instance used to store job shards.
    /// </summary>
    public BlobServiceClient BlobServiceClient { get; set; } = null!;

    /// <summary>
    /// Gets or sets the name of the container used to store durable jobs.
    /// </summary>
    public string ContainerName { get; set; } = "jobs";

    /// <summary>
    /// Gets or sets the maximum number of job operations to batch together in a single blob write.
    /// Default is 50 operations.
    /// </summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets the minimum number of job operations to batch together before flushing.
    /// If more than 1 then the we will wait <see cref="BatchFlushInterval"/> for additional operations.
    /// Default is 1 operation (immediate flush, optimized for latency).
    /// </summary>
    public int MinBatchSize { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum time to wait for additional operations if the minimum batch size isn't reached 
    /// before flushing a batch.
    /// Default is 50 milliseconds.
    /// </summary>
    public TimeSpan BatchFlushInterval { get; set; } = TimeSpan.FromMilliseconds(50);
    
    /// <summary>
    /// Gets or sets the maximum number of retries for creating a blob for a job shard in case of name collisions.
    /// </summary>
    public int MaxBlobCreationRetries { get; internal set; } = 3;
}
