using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs.AzureStorage;

public sealed class AzureStorageJobShardManager : JobShardManager
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private BlobContainerClient _client = null!;
    private readonly TimeSpan _maxShardDuration;
    private readonly IClusterMembershipService _clusterMembership;

    public AzureStorageJobShardManager(BlobServiceClient client, string containerName, TimeSpan maxShardDuration, IClusterMembershipService clusterMembership)
    {
        _blobServiceClient = client;
        _containerName = containerName;
        _maxShardDuration = maxShardDuration;
        _clusterMembership = clusterMembership;
    }

    public AzureStorageJobShardManager(IOptions<AzureStorageJobShardOptions> options, IClusterMembershipService clusterMembership)
        : this(options.Value.BlobServiceClient, options.Value.ContainerName, options.Value.MaxShardDuration, clusterMembership)
    {
    }

    public override async Task<List<JobShard>> GetJobShardsAsync(SiloAddress siloAddress, DateTimeOffset maxDateTime)
    {
        await InitializeIfNeeded();
        var blobs = _client.GetBlobsAsync(traits: BlobTraits.Metadata);
        var result = new List<JobShard>();
        await foreach (var blob in blobs)
        {
            // Get the owner of the shard
            var (owner, minDueTime, maxDueTime) = ParseMetadata(blob.Metadata);

            if (minDueTime > maxDateTime)
            {
                // This shard is too new, stop there
                break;
            }

            if (owner == null || _clusterMembership.CurrentSnapshot.GetSiloStatus(owner) == SiloStatus.Dead)
            {
                // The owner is dead or unknown, we can take over this shard
                var blobClient = _client.GetAppendBlobClient(blob.Name);
                var metadata = blob.Metadata;
                metadata["Owner"] = siloAddress.ToParsableString();
                try
                {
                    await blobClient.SetMetadataAsync(metadata, conditions: new BlobRequestConditions { IfMatch = blob.Properties.ETag });
                }
                catch (RequestFailedException)
                {
                    // Someone else took over the shard
                    continue;
                }
                var shard = new AzureStorageJobShard(blob.Name, minDueTime, maxDueTime, blobClient);
                await shard.InitializeAsync();
                await shard.MarkAsComplete();
                result.Add(shard);
            }
        }
        return result;
    }

    public override async Task<JobShard> RegisterShard(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata)
    {
        await InitializeIfNeeded();
        for (var i = 0;; i++) // TODO limit the number of attempts
        {
            var shardId = $"{minDueTime:yyyyMMddHHmm}-{siloAddress.ToParsableString()}-{i}";
            var blobClient = _client.GetAppendBlobClient(shardId);
            var metadataInfo = CreateMetadata(siloAddress, minDueTime, maxDueTime);
            try
            {
                var response = await blobClient.CreateIfNotExistsAsync(metadata: metadataInfo);
                if (response == null)
                {
                    // Blob already exists, try again with a different name
                    continue;
                }
            }
            catch (RequestFailedException)
            {
                if (i > 100) throw; // Prevent infinite loop
                // Blob already exists, try again with a different name
                continue;
            }
            var shard = new AzureStorageJobShard(shardId, minDueTime, maxDueTime, blobClient);
            await shard.InitializeAsync();
            return shard;
        }
    }

    public override async Task UnregisterShard(SiloAddress siloAddress, JobShard shard)
    {
        var azureShard = shard as AzureStorageJobShard ?? throw new ArgumentException("Shard is not an AzureStorageJobShard", nameof(shard));
        var conditions = new BlobRequestConditions { IfMatch = azureShard.ETag };
        var count = await shard.GetJobCount();
        var properties = await azureShard.BlobClient.GetPropertiesAsync(conditions);
        if (count > 0)
        {
            // There are still jobs in the shard, unregister it
            var metadata = properties.Value.Metadata;
            var (owner, _, _) = ParseMetadata(metadata);

            if (owner != siloAddress)
                throw new InvalidOperationException("Cannot unregister a shard owned by another silo");

            metadata.Remove("Owner");
            var response = await azureShard.BlobClient.SetMetadataAsync(metadata, conditions);
        }
        else
        {
            // No jobs left, we can delete the shard
            await azureShard.BlobClient.DeleteIfExistsAsync(conditions: conditions);
        }
    }

    private async ValueTask InitializeIfNeeded()
    {
        if (_client != null) return;

        _client = _blobServiceClient.GetBlobContainerClient(_containerName);
        await _client.CreateIfNotExistsAsync();
    }

    private static Dictionary<string, string> CreateMetadata(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime)
    {
        return new Dictionary<string, string>
        {
            { "Owner", siloAddress.ToParsableString() },
            { "MinDueTime", minDueTime.ToString("o") },
            { "MaxDueTime", maxDueTime.ToString("o") }
        };
    }

    private static (SiloAddress? owner, DateTime minDueTime, DateTime maxDueTime) ParseMetadata(IDictionary<string, string> metadata)
    {
        var owner = metadata.TryGetValue("Owner", out var ownerStr) ? SiloAddress.FromParsableString(ownerStr) : null;
        var minDueTime = metadata.TryGetValue("MinDueTime", out var minDueTimeStr) && DateTime.TryParse(minDueTimeStr, out var minDt) ? minDt : DateTime.MinValue;
        var maxDueTime = metadata.TryGetValue("MaxDueTime", out var maxDueTimeStr) && DateTime.TryParse(maxDueTimeStr, out var maxDt) ? maxDt : DateTime.MaxValue;
        return (owner, minDueTime.ToUniversalTime(), maxDueTime.ToUniversalTime());
    }
}
