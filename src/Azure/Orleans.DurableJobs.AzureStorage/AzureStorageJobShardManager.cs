using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.DurableJobs.AzureStorage;

public sealed partial class AzureStorageJobShardManager : JobShardManager
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly string _blobPrefix;
    private BlobContainerClient _client = null!;
    private readonly IClusterMembershipService _clusterMembership;
    private readonly ConcurrentDictionary<string, AzureStorageJobShard> _jobShardCache = new();
    private readonly ILogger<AzureStorageJobShardManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly AzureStorageJobShardOptions _options;
    private long _shardCounter = 0; // For generating unique shard IDs

    public AzureStorageJobShardManager(
        SiloAddress siloAddress,
        BlobServiceClient client,
        string containerName,
        string blobPrefix,
        AzureStorageJobShardOptions options,
        IClusterMembershipService clusterMembership,
        ILoggerFactory loggerFactory)
        : base(siloAddress)
    {
        _blobServiceClient = client;
        _containerName = containerName;
        _blobPrefix = blobPrefix;
        _clusterMembership = clusterMembership;
        _logger = loggerFactory.CreateLogger<AzureStorageJobShardManager>();
        _loggerFactory = loggerFactory;
        _options = options;
    }

    public AzureStorageJobShardManager(
        ILocalSiloDetails localSiloDetails,
        IOptions<AzureStorageJobShardOptions> options,
        IClusterMembershipService clusterMembership,
        ILoggerFactory loggerFactory)
        : this(localSiloDetails.SiloAddress, options.Value.BlobServiceClient, options.Value.ContainerName, localSiloDetails.ClusterId,  options.Value, clusterMembership, loggerFactory)
    {
    }

    public override async Task<List<Orleans.DurableJobs.IJobShard>> AssignJobShardsAsync(DateTimeOffset maxShardStartTime, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken);
        LogAssigningShards(_logger, SiloAddress, maxShardStartTime, _containerName);

        var result = new List<Orleans.DurableJobs.IJobShard>();
        await foreach (var blob in _client.GetBlobsAsync(traits: BlobTraits.Metadata, cancellationToken: cancellationToken, prefix: _blobPrefix))
        {
            // Get the owner and creator of the shard
            var (owner, membershipVersion, shardStartTime, maxDueTime) = ParseMetadata(blob.Metadata);

            // Check if the membership version is more recent than our current version
            if (membershipVersion > _clusterMembership.CurrentSnapshot.Version)
            {
                // Refresh membership to at least that version
                await _clusterMembership.Refresh(membershipVersion, cancellationToken);
            }

            if (shardStartTime > maxShardStartTime)
            {
                // This shard is too new. Since blobs are returned in alphabetical order and our blob names
                // contain timestamps (yyyyMMddHHmm format), all subsequent blobs will also be too new.
                LogShardTooNew(_logger, blob.Name, shardStartTime, maxShardStartTime);
                break;
            }

            // If I am the owner, the shard must be in cache - always return it
            if (owner is not null && owner.Equals(SiloAddress))
            {
                if (_jobShardCache.TryGetValue(blob.Name, out var shard))
                {
                    LogShardAssigned(_logger, blob.Name, SiloAddress);
                    result.Add(shard);
                }
                else
                {
                    // Shard is owned by us but not in cache - this is unexpected, release ownership
                    Debug.Assert(false, $"Shard '{blob.Name}' is owned by this silo but not in cache - releasing ownership");
                    await ReleaseOwnership(blob.Name);
                }
                continue;
            }

            // In debug, verify that if we're not the owner, the shard should not be in our cache
            Debug.Assert(!_jobShardCache.ContainsKey(blob.Name), $"Shard '{blob.Name}' is in cache but we are not the owner (owner: {owner?.ToParsableString() ?? "none"})");

            // Check if the owner is valid
            var ownerStatus = owner is not null ? _clusterMembership.CurrentSnapshot.GetSiloStatus(owner) : SiloStatus.None;

            if (ownerStatus is not SiloStatus.Dead and not SiloStatus.None)
            {
                // Owner is still active and it's not me, skip this shard
                LogShardStillOwned(_logger, blob.Name, owner!);
                continue;
            }
            else
            {
                // Try to claim orphaned shard
                LogClaimingShard(_logger, blob.Name, SiloAddress, owner);
                var blobClient = _client.GetAppendBlobClient(blob.Name);
                var metadata = blob.Metadata;
                var orphanedShard = new AzureStorageJobShard(blob.Name, shardStartTime, maxDueTime, blobClient, metadata, blob.Properties.ETag, _options, _loggerFactory.CreateLogger<AzureStorageJobShard>());
                if (!await TryTakeOwnership(orphanedShard, metadata, SiloAddress, cancellationToken))
                {
                    // Someone else took over the shard, dispose and continue
                    await orphanedShard.DisposeAsync();
                    LogShardOwnershipConflict(_logger, blob.Name, SiloAddress);
                    continue;
                }
                await orphanedShard.InitializeAsync(cancellationToken);
                // We don't want to add new jobs to shards that we just took ownership of
                await orphanedShard.MarkAsCompleteAsync(cancellationToken);
                _jobShardCache[blob.Name] = orphanedShard;
                LogShardAssigned(_logger, blob.Name, SiloAddress);
                result.Add(orphanedShard);
            }
        }
        
        LogAssignmentCompleted(_logger, result.Count, SiloAddress);
        return result;

        async Task ReleaseOwnership(string blobName)
        {
            try
            {
                var blobClient = _client.GetAppendBlobClient(blobName);
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                var metadata = properties.Value.Metadata;
                metadata.Remove("Owner");
                await blobClient.SetMetadataAsync(metadata, new BlobRequestConditions { IfMatch = properties.Value.ETag }, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log but continue - we'll let another silo claim it
                _logger.LogWarning(ex, "Failed to release ownership of shard '{ShardId}' that was not in cache", blobName);
            }
        }

        async Task<bool> TryTakeOwnership(AzureStorageJobShard shard, IDictionary<string, string> metadata, SiloAddress newOwner, CancellationToken ct)
        {
            metadata["Owner"] = newOwner.ToParsableString();
            metadata["MembershipVersion"] = _clusterMembership.CurrentSnapshot.Version.Value.ToString();
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

    public override async Task<Orleans.DurableJobs.IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        await InitializeIfNeeded(cancellationToken);
        LogRegisteringShard(_logger, SiloAddress, minDueTime, maxDueTime, _containerName);
        
        var i = 0;
        while (true)
        {
            var counter = Interlocked.Increment(ref _shardCounter);
            var shardId = $"{_blobPrefix}-{minDueTime:yyyyMMddHHmm}-{SiloAddress.ToParsableString()}-{counter}";
            var blobClient = _client.GetAppendBlobClient(shardId);
            var metadataInfo = CreateMetadata(metadata, SiloAddress, _clusterMembership.CurrentSnapshot.Version, minDueTime, maxDueTime);
            metadataInfo["Owner"] = SiloAddress.ToParsableString();
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
                i++;
                if (i > _options.MaxBlobCreationRetries)
                {
                    throw new InvalidOperationException($"Failed to create shard blob '{shardId}' after {i} attempts", ex);
                }
                // Blob already exists, try again with a different name
                LogShardRegistrationRetry(_logger, ex, shardId, i);
                continue;
            }
            
            var shard = new AzureStorageJobShard(shardId, minDueTime, maxDueTime, blobClient, metadataInfo, null, _options, _loggerFactory.CreateLogger<AzureStorageJobShard>());
            await shard.InitializeAsync(cancellationToken);
            _jobShardCache[shardId] = shard;
            LogShardRegistered(_logger, shardId, SiloAddress);
            return shard;
        }
    }

    public override async Task UnregisterShardAsync(Orleans.DurableJobs.IJobShard shard, CancellationToken cancellationToken)
    {
        var azureShard = shard as AzureStorageJobShard ?? throw new ArgumentException("Shard is not an AzureStorageJobShard", nameof(shard));
        LogUnregisteringShard(_logger, shard.Id, SiloAddress);
        
        // Stop the background storage processor to ensure no more changes can happen
        await azureShard.StopProcessorAsync(cancellationToken);
        
        // Now we can safely get a consistent view of the state
        var count = await shard.GetJobCountAsync();
        // We want to make sure to get the latest properties
        var properties = await azureShard.BlobClient.GetPropertiesAsync(cancellationToken: cancellationToken);

        // But we don't want to update the metadata if the ETag has changed
        var currentETag = properties.Value.ETag;
        var conditions = new BlobRequestConditions { IfMatch = currentETag };
        var metadata = properties.Value.Metadata;
        var (owner, _, _, _) = ParseMetadata(metadata);

        if (owner != SiloAddress)
        {
            LogUnregisterWrongOwner(_logger, shard.Id, SiloAddress, owner);
            throw new InvalidOperationException("Cannot unregister a shard owned by another silo");
        }

        if (count > 0)
        {
            // There are still jobs in the shard, unregister it
            metadata.Remove("Owner");
            var response = await azureShard.BlobClient.SetMetadataAsync(metadata, conditions, cancellationToken);
            _jobShardCache.TryRemove(shard.Id, out _);
            LogShardOwnershipReleased(_logger, shard.Id, SiloAddress, count);
        }
        else
        {
            // No jobs left, we can delete the shard
            await azureShard.BlobClient.DeleteIfExistsAsync(conditions: conditions, cancellationToken: cancellationToken);
            _jobShardCache.TryRemove(shard.Id, out _);
            LogShardDeleted(_logger, shard.Id, SiloAddress);
        }

        // Dispose the shard's resources
        await azureShard.DisposeAsync();
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
            { "MinDueTime", minDueTime.ToString("o") },
            { "MaxDueTime", maxDueTime.ToString("o") },
            { "MembershipVersion", membershipVersion.Value.ToString(CultureInfo.InvariantCulture) }
        };

        return metadata;
    }

    private static (SiloAddress? owner, MembershipVersion membershipVersion, DateTimeOffset minDueTime, DateTimeOffset maxDueTime) ParseMetadata(IDictionary<string, string> metadata)
    {
        var owner = metadata.TryGetValue("Owner", out var ownerStr) ? SiloAddress.FromParsableString(ownerStr) : null;
        var membershipVersion = metadata.TryGetValue("MembershipVersion", out var membershipVersionStr) && long.TryParse(membershipVersionStr, out var versionValue)
            ? new MembershipVersion(versionValue)
            : MembershipVersion.MinValue;
        var minDueTime = metadata.TryGetValue("MinDueTime", out var minDueTimeStr) && DateTimeOffset.TryParse(minDueTimeStr, out var minDt) ? minDt : DateTimeOffset.MinValue;
        var maxDueTime = metadata.TryGetValue("MaxDueTime", out var maxDueTimeStr) && DateTimeOffset.TryParse(maxDueTimeStr, out var maxDt) ? maxDt : DateTimeOffset.MaxValue;
        return (owner, membershipVersion, minDueTime, maxDueTime);
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
        Message = "Ignoring shard '{ShardId}' since its start time is greater than specified maximum (MinDueTime={MinDueTime}, MaxDateTime={MaxDateTime})"
    )]
    private static partial void LogShardTooNew(ILogger logger, string shardId, DateTimeOffset minDueTime, DateTimeOffset maxDateTime);

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
        Message = "Claiming shard '{ShardId}' for silo {SiloAddress} (Previous Owner={PreviousOwner})"
    )]
    private static partial void LogClaimingShard(ILogger logger, string shardId, SiloAddress siloAddress, SiloAddress? previousOwner);

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
        Message = "Creating new shard for silo {SiloAddress} (MinDueTime={MinDueTime}, MaxDueTime={MaxDueTime}) in container '{ContainerName}'"
    )]
    private static partial void LogRegisteringShard(ILogger logger, SiloAddress siloAddress, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, string containerName);

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
        Message = "Shard '{ShardId}' created successfully for silo {SiloAddress}"
    )]
    private static partial void LogShardRegistered(ILogger logger, string shardId, SiloAddress siloAddress);

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
