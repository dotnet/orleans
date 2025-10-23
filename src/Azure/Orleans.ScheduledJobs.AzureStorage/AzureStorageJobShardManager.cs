using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs.AzureStorage;

public sealed class AzureStorageJobShardManager : JobShardManager
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private BlobContainerClient _client = null!;
    private readonly IClusterMembershipService _clusterMembership;
    private readonly ConcurrentDictionary<string, AzureStorageJobShard> _jobShardCache = new();

    public AzureStorageJobShardManager(
        BlobServiceClient client,
        string containerName,
        IClusterMembershipService clusterMembership)
    {
        _blobServiceClient = client;
        _containerName = containerName;
        _clusterMembership = clusterMembership;
    }

    public AzureStorageJobShardManager(IOptions<AzureStorageJobShardOptions> options, IClusterMembershipService clusterMembership)
        : this(options.Value.BlobServiceClient, options.Value.ContainerName, clusterMembership)
    {
    }

    public override async Task<List<JobShard>> AssignJobShardsAsync(SiloAddress siloAddress, DateTimeOffset maxDateTime, CancellationToken cancellationToken = default)
    {
        await InitializeIfNeeded(cancellationToken);
        var blobs = _client.GetBlobsAsync(traits: BlobTraits.Metadata, cancellationToken: cancellationToken);
        var result = new List<JobShard>();
        await foreach (var blob in blobs.WithCancellation(cancellationToken))
        {
            // Get the owner and creator of the shard
            var (owner, creator, minDueTime, maxDueTime) = ParseMetadata(blob.Metadata);

            if (minDueTime > maxDateTime)
            {
                // This shard is too new, stop there
                break;
            }

            // Check if the owner is valid
            var ownerIsActive = owner is not null && _clusterMembership.CurrentSnapshot.GetSiloStatus(owner) == SiloStatus.Active;

            if (ownerIsActive)
            {
                // Owner is still active, skip this shard
                continue;
            }

            // Check if the creator is still active
            var creatorIsActive = creator is not null && _clusterMembership.CurrentSnapshot.GetSiloStatus(creator) == SiloStatus.Active;

            AzureStorageJobShard? shard = null;
            
            // If I am the creator, I can reclaim the shard, use the cache to avoid re-initializing
            if (_jobShardCache.TryGetValue(blob.Name, out shard))
            {
                var blobClient = shard.BlobClient;
                var metadata = blob.Metadata;
                metadata["Owner"] = siloAddress.ToParsableString();
                if (!await TryTakeOwnership(shard, metadata, siloAddress, cancellationToken))
                {
                    // Someone else took over the shard, remove from cache
                    _jobShardCache.TryRemove(blob.Name, out _);
                    continue;
                }
            }
            else if (!creatorIsActive || siloAddress.Equals(creator))
            {
                // If I am not the creator, only reclaim if the creator is not active anymore,
                // or if for some reason I am the creator but I don't have the shard in cache (should not happen normally)
                var blobClient = _client.GetAppendBlobClient(blob.Name);
                var metadata = blob.Metadata;
                metadata["Owner"] = siloAddress.ToParsableString();
                shard = new AzureStorageJobShard(blob.Name, minDueTime, maxDueTime, blobClient, blob.Properties.ETag);
                if (!await TryTakeOwnership(shard, metadata, siloAddress, cancellationToken))
                {
                    // Someone else took over the shard
                    continue;
                }
                await shard.InitializeAsync();
                await shard.MarkAsComplete();
            }

            if (shard != null)
            {
                result.Add(shard);
            }
        }
        return result;

        static async Task<bool> TryTakeOwnership(AzureStorageJobShard shard, IDictionary<string, string> metadata, SiloAddress newOwner, CancellationToken ct)
        {
            metadata["Owner"] = newOwner.ToParsableString();
            try
            {
                await shard.UpdateBlobMetadata(metadata, ct);
                return true;
            }
            catch (RequestFailedException)
            {
                // Someone else took over the shard
                // TODO LOG
                return false;
            }
        }
    }

    public override async Task<JobShard> RegisterShard(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, bool assignToCreator = true, CancellationToken cancellationToken = default)
    {
        await InitializeIfNeeded(cancellationToken);
        for (var i = 0;; i++) // TODO limit the number of attempts
        {
            var shardId = $"{minDueTime:yyyyMMddHHmm}-{siloAddress.ToParsableString()}-{i}"; // todo make sure this is a valid blob name
            var blobClient = _client.GetAppendBlobClient(shardId);
            var metadataInfo = CreateMetadata(siloAddress, minDueTime, maxDueTime); // TODO MERGE with input metadata
            if (assignToCreator)
            {
                metadataInfo["Owner"] = siloAddress.ToParsableString();
            }
            try
            {
                var response = await blobClient.CreateIfNotExistsAsync(metadata: metadataInfo, cancellationToken: cancellationToken);
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
            _jobShardCache[shardId] = shard;
            return shard;
        }
    }

    public override async Task UnregisterShard(SiloAddress siloAddress, JobShard shard, CancellationToken cancellationToken = default)
    {
        var azureShard = shard as AzureStorageJobShard ?? throw new ArgumentException("Shard is not an AzureStorageJobShard", nameof(shard));
        var conditions = new BlobRequestConditions { IfMatch = azureShard.ETag };
        var count = await shard.GetJobCount();
        var properties = await azureShard.BlobClient.GetPropertiesAsync(conditions, cancellationToken);
        if (count > 0)
        {
            // There are still jobs in the shard, unregister it
            var metadata = properties.Value.Metadata;
            var (owner, _, _, _) = ParseMetadata(metadata);

            if (owner != siloAddress)
                throw new InvalidOperationException("Cannot unregister a shard owned by another silo");

            metadata.Remove("Owner");
            var response = await azureShard.BlobClient.SetMetadataAsync(metadata, conditions, cancellationToken);
            _jobShardCache.TryRemove(shard.Id, out _);
        }
        else
        {
            // No jobs left, we can delete the shard
            await azureShard.BlobClient.DeleteIfExistsAsync(conditions: conditions, cancellationToken: cancellationToken);
        }
    }

    private async ValueTask InitializeIfNeeded(CancellationToken cancellationToken = default)
    {
        if (_client != null) return;

        _client = _blobServiceClient.GetBlobContainerClient(_containerName);
        await _client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    private static Dictionary<string, string> CreateMetadata(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime)
    {
        var metadata = new Dictionary<string, string>
        {
            { "Creator", siloAddress.ToParsableString() },
            { "MinDueTime", minDueTime.ToString("o") },
            { "MaxDueTime", maxDueTime.ToString("o") }
        };

        return metadata;
    }

    private static (SiloAddress? owner, SiloAddress? creator, DateTime minDueTime, DateTime maxDueTime) ParseMetadata(IDictionary<string, string> metadata)
    {
        var owner = metadata.TryGetValue("Owner", out var ownerStr) ? SiloAddress.FromParsableString(ownerStr) : null;
        var creator = metadata.TryGetValue("Creator", out var creatorStr) ? SiloAddress.FromParsableString(creatorStr) : null;
        var minDueTime = metadata.TryGetValue("MinDueTime", out var minDueTimeStr) && DateTime.TryParse(minDueTimeStr, out var minDt) ? minDt : DateTime.MinValue;
        var maxDueTime = metadata.TryGetValue("MaxDueTime", out var maxDueTimeStr) && DateTime.TryParse(maxDueTimeStr, out var maxDt) ? maxDt : DateTime.MaxValue;
        return (owner, creator, minDueTime.ToUniversalTime(), maxDueTime.ToUniversalTime());
    }
}
