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

public sealed partial class AzureStorageJobShardManager : JobShardManager
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private BlobContainerClient _client = null!;
    private readonly IClusterMembershipService _clusterMembership;
    private readonly ConcurrentDictionary<string, AzureStorageJobShard> _jobShardCache = new();
    private readonly ILogger<AzureStorageJobShardManager> _logger;

    public AzureStorageJobShardManager(
        BlobServiceClient client,
        string containerName,
        IClusterMembershipService clusterMembership,
        ILogger<AzureStorageJobShardManager> logger)
    {
        _blobServiceClient = client;
        _containerName = containerName;
        _clusterMembership = clusterMembership;
        _logger = logger;
    }

    public AzureStorageJobShardManager(
        IOptions<AzureStorageJobShardOptions> options,
        IClusterMembershipService clusterMembership,
        ILogger<AzureStorageJobShardManager> logger)
        : this(options.Value.BlobServiceClient, options.Value.ContainerName, clusterMembership, logger)
    {
    }

    public override async Task<List<IJobShard>> AssignJobShardsAsync(SiloAddress siloAddress, DateTimeOffset maxDateTime, CancellationToken cancellationToken = default)
    {
        await InitializeIfNeeded(cancellationToken);
        LogAssigningShards(_logger, siloAddress, maxDateTime, _containerName);

        var blobs = _client.GetBlobsAsync(traits: BlobTraits.Metadata, cancellationToken: cancellationToken);
        var result = new List<IJobShard>();
        await foreach (var blob in blobs.WithCancellation(cancellationToken))
        {
            // Get the owner and creator of the shard
            var (owner, creator, membershipVersion, minDueTime, maxDueTime) = ParseMetadata(blob.Metadata);

            // Check if the membership version is more recent than our current version
            if (membershipVersion > _clusterMembership.CurrentSnapshot.Version)
            {
                // Refresh membership to at least that version
                await _clusterMembership.Refresh(membershipVersion);
            }

            if (minDueTime > maxDateTime)
            {
                // This shard is too new, stop there
                LogShardTooNew(_logger, blob.Name, minDueTime, maxDateTime);
                break;
            }

            // Check if the owner is valid
            var ownerIsActive = owner is not null && _clusterMembership.CurrentSnapshot.GetSiloStatus(owner) == SiloStatus.Active;

            if (ownerIsActive)
            {
                // Owner is still active, skip this shard
                LogShardStillOwned(_logger, blob.Name, owner!);
                continue;
            }

            // Check if the creator is still active
            var creatorIsActive = creator is not null && _clusterMembership.CurrentSnapshot.GetSiloStatus(creator) == SiloStatus.Active;

            AzureStorageJobShard? shard = null;
            
            // If I am the creator, I can reclaim the shard, use the cache to avoid re-initializing
            if (_jobShardCache.TryGetValue(blob.Name, out shard))
            {
                LogReclaimingShardFromCache(_logger, blob.Name, siloAddress);
                var blobClient = shard.BlobClient;
                var metadata = blob.Metadata;
                if (!await TryTakeOwnership(shard, metadata, siloAddress, cancellationToken))
                {
                    // Someone else took over the shard, remove from cache
                    _jobShardCache.TryRemove(blob.Name, out _);
                    LogShardOwnershipConflict(_logger, blob.Name, siloAddress);
                    continue;
                }
            }
            else if (!creatorIsActive || siloAddress.Equals(creator))
            {
                // If I am not the creator, only reclaim if the creator is not active anymore,
                // or if for some reason I am the creator but I don't have the shard in cache (should not happen normally)
                LogClaimingShard(_logger, blob.Name, siloAddress, creatorIsActive, siloAddress.Equals(creator));
                var blobClient = _client.GetAppendBlobClient(blob.Name);
                var metadata = blob.Metadata;
                shard = new AzureStorageJobShard(blob.Name, minDueTime, maxDueTime, blobClient, metadata, blob.Properties.ETag);
                if (!await TryTakeOwnership(shard, metadata, siloAddress, cancellationToken))
                {
                    // Someone else took over the shard
                    LogShardOwnershipConflict(_logger, blob.Name, siloAddress);
                    continue;
                }
                await shard.InitializeAsync(cancellationToken);
                await shard.MarkAsCompleteAsync(cancellationToken);
            }

            if (shard != null)
            {
                LogShardAssigned(_logger, blob.Name, siloAddress);
                result.Add(shard);
            }
        }
        
        LogAssignmentCompleted(_logger, result.Count, siloAddress);
        return result;

        async Task<bool> TryTakeOwnership(AzureStorageJobShard shard, IDictionary<string, string> metadata, SiloAddress newOwner, CancellationToken ct)
        {
            metadata["Owner"] = newOwner.ToParsableString();
            metadata["MembershipVersion"] = _clusterMembership.CurrentSnapshot.Version.ToString();
            try
            {
                await shard.UpdateBlobMetadata(metadata, ct);
                LogOwnershipTaken(_logger, shard.Id, newOwner);
                return true;
            }
            catch (RequestFailedException ex)
            {
                // Someone else took over the shard
                LogOwnershipFailed(_logger, ex, shard.Id, newOwner);
                return false;
            }
        }
    }

    public override async Task<IJobShard> RegisterShard(SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, bool assignToCreator = true, CancellationToken cancellationToken = default)
    {
        await InitializeIfNeeded(cancellationToken);
        LogRegisteringShard(_logger, siloAddress, minDueTime, maxDueTime, assignToCreator, _containerName);
        
        for (var i = 0;; i++) // TODO limit the number of attempts
        {
            var shardId = $"{minDueTime:yyyyMMddHHmm}-{siloAddress.ToParsableString()}-{i}"; // todo make sure this is a valid blob name
            var blobClient = _client.GetAppendBlobClient(shardId);
            var metadataInfo = CreateMetadata(metadata, siloAddress, _clusterMembership.CurrentSnapshot.Version, minDueTime, maxDueTime);
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
                    LogShardIdCollision(_logger, shardId, i);
                    continue;
                }
            }
            catch (RequestFailedException ex)
            {
                if (i > 100) throw; // Prevent infinite loop
                // Blob already exists, try again with a different name
                LogShardRegistrationRetry(_logger, ex, shardId, i);
                continue;
            }
            
            var shard = new AzureStorageJobShard(shardId, minDueTime, maxDueTime, blobClient, metadata, null);
            await shard.InitializeAsync(cancellationToken);
            _jobShardCache[shardId] = shard;
            LogShardRegistered(_logger, shardId, siloAddress, assignToCreator);
            return shard;
        }
    }

    public override async Task UnregisterShard(SiloAddress siloAddress, IJobShard shard, CancellationToken cancellationToken = default)
    {
        var azureShard = shard as AzureStorageJobShard ?? throw new ArgumentException("Shard is not an AzureStorageJobShard", nameof(shard));
        LogUnregisteringShard(_logger, shard.Id, siloAddress);
        
        var conditions = new BlobRequestConditions { IfMatch = azureShard.ETag };
        var count = await shard.GetJobCount();
        var properties = await azureShard.BlobClient.GetPropertiesAsync(conditions, cancellationToken);
        if (count > 0)
        {
            // There are still jobs in the shard, unregister it
            var metadata = properties.Value.Metadata;
            var (owner, _, _, _, _) = ParseMetadata(metadata);

            if (owner != siloAddress)
            {
                LogUnregisterWrongOwner(_logger, shard.Id, siloAddress, owner);
                throw new InvalidOperationException("Cannot unregister a shard owned by another silo");
            }

            metadata.Remove("Owner");
            var response = await azureShard.BlobClient.SetMetadataAsync(metadata, conditions, cancellationToken);
            _jobShardCache.TryRemove(shard.Id, out _);
            LogShardOwnershipReleased(_logger, shard.Id, siloAddress, count);
        }
        else
        {
            // No jobs left, we can delete the shard
            await azureShard.BlobClient.DeleteIfExistsAsync(conditions: conditions, cancellationToken: cancellationToken);
            LogShardDeleted(_logger, shard.Id, siloAddress);
        }
    }

    private async ValueTask InitializeIfNeeded(CancellationToken cancellationToken = default)
    {
        if (_client != null) return;

        LogInitializing(_logger, _containerName);
        _client = _blobServiceClient.GetBlobContainerClient(_containerName);
        await _client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        LogInitialized(_logger, _containerName);
    }

    private static Dictionary<string, string> CreateMetadata(IDictionary<string, string> existingMetadata, SiloAddress siloAddress, MembershipVersion membershipVersion, DateTimeOffset minDueTime, DateTimeOffset maxDueTime)
    {
        var metadata = new Dictionary<string, string>(existingMetadata)
        {
            { "Creator", siloAddress.ToParsableString() },
            { "MinDueTime", minDueTime.ToString("o") },
            { "MaxDueTime", maxDueTime.ToString("o") },
            { "MembershipVersion", membershipVersion.ToString() }
        };

        return metadata;
    }

    private static (SiloAddress? owner, SiloAddress? creator, MembershipVersion membershipVersion, DateTime minDueTime, DateTime maxDueTime) ParseMetadata(IDictionary<string, string> metadata)
    {
        var owner = metadata.TryGetValue("Owner", out var ownerStr) ? SiloAddress.FromParsableString(ownerStr) : null;
        var creator = metadata.TryGetValue("Creator", out var creatorStr) ? SiloAddress.FromParsableString(creatorStr) : null;
        var membershipVersion = metadata.TryGetValue("MembershipVersion", out var membershipVersionStr) && long.TryParse(membershipVersionStr, out var versionValue)
            ? new MembershipVersion(versionValue)
            : MembershipVersion.MinValue;
        var minDueTime = metadata.TryGetValue("MinDueTime", out var minDueTimeStr) && DateTime.TryParse(minDueTimeStr, out var minDt) ? minDt : DateTime.MinValue;
        var maxDueTime = metadata.TryGetValue("MaxDueTime", out var maxDueTimeStr) && DateTime.TryParse(maxDueTimeStr, out var maxDt) ? maxDt : DateTime.MaxValue;
        return (owner, creator, membershipVersion, minDueTime.ToUniversalTime(), maxDueTime.ToUniversalTime());
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Initializing Azure Storage Job Shard Manager with container '{ContainerName}'"
    )]
    private static partial void LogInitializing(ILogger logger, string containerName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Azure Storage Job Shard Manager initialized successfully for container '{ContainerName}'"
    )]
    private static partial void LogInitialized(ILogger logger, string containerName);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Assigning job shards for silo {SiloAddress} with max time {MaxDateTime} from container '{ContainerName}'"
    )]
    private static partial void LogAssigningShards(ILogger logger, SiloAddress siloAddress, DateTimeOffset maxDateTime, string containerName);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Shard '{ShardId}' is too new (MinDueTime={MinDueTime}, MaxDateTime={MaxDateTime})"
    )]
    private static partial void LogShardTooNew(ILogger logger, string shardId, DateTime minDueTime, DateTimeOffset maxDateTime);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Shard '{ShardId}' is still owned by active silo {Owner}"
    )]
    private static partial void LogShardStillOwned(ILogger logger, string shardId, SiloAddress owner);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Reclaiming shard '{ShardId}' from cache for silo {SiloAddress}"
    )]
    private static partial void LogReclaimingShardFromCache(ILogger logger, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Claiming shard '{ShardId}' for silo {SiloAddress} (CreatorIsActive={CreatorIsActive}, IsCreator={IsCreator})"
    )]
    private static partial void LogClaimingShard(ILogger logger, string shardId, SiloAddress siloAddress, bool creatorIsActive, bool isCreator);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to take ownership of shard '{ShardId}' for silo {SiloAddress} due to conflict"
    )]
    private static partial void LogShardOwnershipConflict(ILogger logger, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard '{ShardId}' assigned to silo {SiloAddress}"
    )]
    private static partial void LogShardAssigned(ILogger logger, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Assigned {ShardCount} shard(s) to silo {SiloAddress}"
    )]
    private static partial void LogAssignmentCompleted(ILogger logger, int shardCount, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Took ownership of shard '{ShardId}' for silo {SiloAddress}"
    )]
    private static partial void LogOwnershipTaken(ILogger logger, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to take ownership of shard '{ShardId}' for silo {SiloAddress}"
    )]
    private static partial void LogOwnershipFailed(ILogger logger, Exception exception, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Registering new shard for silo {SiloAddress} (MinDueTime={MinDueTime}, MaxDueTime={MaxDueTime}, AssignToCreator={AssignToCreator}) in container '{ContainerName}'"
    )]
    private static partial void LogRegisteringShard(ILogger logger, SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, bool assignToCreator, string containerName);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Shard ID collision for '{ShardId}' (attempt {Attempt}), retrying with new ID"
    )]
    private static partial void LogShardIdCollision(ILogger logger, string shardId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to register shard '{ShardId}' (attempt {Attempt}), retrying"
    )]
    private static partial void LogShardRegistrationRetry(ILogger logger, Exception exception, string shardId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shard '{ShardId}' registered successfully for silo {SiloAddress} (AssignedToCreator={AssignedToCreator})"
    )]
    private static partial void LogShardRegistered(ILogger logger, string shardId, SiloAddress siloAddress, bool assignedToCreator);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Unregistering shard '{ShardId}' for silo {SiloAddress}"
    )]
    private static partial void LogUnregisteringShard(ILogger logger, string shardId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cannot unregister shard '{ShardId}' - silo {SiloAddress} is not the owner (Owner={Owner})"
    )]
    private static partial void LogUnregisterWrongOwner(ILogger logger, string shardId, SiloAddress siloAddress, SiloAddress? owner);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Released ownership of shard '{ShardId}' by silo {SiloAddress} ({JobCount} jobs remaining)"
    )]
    private static partial void LogShardOwnershipReleased(ILogger logger, string shardId, SiloAddress siloAddress, int jobCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deleted shard '{ShardId}' by silo {SiloAddress} (no jobs remaining)"
    )]
    private static partial void LogShardDeleted(ILogger logger, string shardId, SiloAddress siloAddress);
}
