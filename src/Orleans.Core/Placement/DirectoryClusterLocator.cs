using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Resolves grain ownership using an <see cref="IClusterDirectory"/>.
/// </summary>
public sealed class DirectoryClusterLocator(
    IClusterDirectory directory,
    IMetaclusterTopologyProvider topologyProvider,
    ClusterPlacementStrategyResolver strategyResolver,
    ClusterPlacementDirectorResolver directorResolver,
    IOptions<MetaclusterOptions> options) : IClusterLocator, IClusterOwnershipValidator
{
    private readonly MetaclusterOptions _options = options.Value;

    /// <inheritdoc/>
    public async ValueTask<ClusterLocation> Locate(
        GrainId grainId,
        ClusterLocationContext context,
        CancellationToken cancellationToken = default)
    {
        var topology = await topologyProvider.GetTopology(cancellationToken);
        var existing = await directory.Lookup(grainId, cancellationToken);
        if (existing is not null)
        {
            if (topology.Clusters.TryGetValue(existing.ClusterId, out var existingCluster)
                && existingCluster.State is MetaclusterClusterState.Active or MetaclusterClusterState.Draining)
            {
                if (string.Equals(existing.ClusterId, context.LocalClusterId, StringComparison.Ordinal))
                {
                    existing = await directory.TryRenew(
                        grainId,
                        existing.Version,
                        context.LocalClusterId,
                        _options.ClusterOwnershipLeaseDuration,
                        cancellationToken)
                        ?? throw new InvalidOperationException($"Ownership lease for grain '{grainId}' could not be renewed.");
                }

                return ToLocation(existing, isExistingOwner: true);
            }

            throw new InvalidOperationException(
                $"Ownership for grain '{grainId}' remains leased to unavailable cluster '{existing.ClusterId}' until '{existing.LeaseExpiration:O}'.");
        }

        var candidateClusters = await GetCandidateClusters(grainId, context, cancellationToken);
        foreach (var clusterId in candidateClusters)
        {
            if (topology.Clusters.TryGetValue(clusterId, out var cluster)
                && cluster.State == MetaclusterClusterState.Active)
            {
                var entry = await directory.GetOrCreate(
                    grainId,
                    clusterId,
                    topology.Epoch,
                    _options.ClusterOwnershipLeaseDuration,
                    cancellationToken);
                return ToLocation(entry, isExistingOwner: false);
            }
        }

        throw new InvalidOperationException(
            $"No active cluster is available to host grain '{grainId}' at topology epoch '{topology.Epoch}'.");
    }

    private async ValueTask<IReadOnlyList<string>> GetCandidateClusters(
        GrainId grainId,
        ClusterLocationContext context,
        CancellationToken cancellationToken)
    {
        if (strategyResolver.Resolve(grainId.Type) is not { } strategy)
        {
            return [context.LocalClusterId];
        }

        var director = directorResolver.Resolve(strategy);
        var result = await director.SelectClusters(strategy, grainId, context, cancellationToken);
        return result.CandidateClusters;
    }

    private static ClusterLocation ToLocation(ClusterDirectoryEntry entry, bool isExistingOwner)
        => new(entry.ClusterId, entry.Version, entry.TopologyEpoch, isExistingOwner);

    /// <inheritdoc/>
    public async ValueTask<ClusterDirectoryEntry> ValidateLocalOwnership(
        GrainId grainId,
        string localClusterId,
        CancellationToken cancellationToken = default)
    {
        var topology = await topologyProvider.GetTopology(cancellationToken);
        if (!topology.Clusters.TryGetValue(localClusterId, out var localCluster)
            || localCluster.State is not (MetaclusterClusterState.Active or MetaclusterClusterState.Draining))
        {
            throw new InvalidOperationException(
                $"Cluster '{localClusterId}' is not an active member of topology epoch '{topology.Epoch}'.");
        }

        var entry = await directory.Lookup(grainId, cancellationToken);
        if (entry is null
            || !string.Equals(entry.ClusterId, localClusterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cluster '{localClusterId}' does not hold a valid ownership lease for grain '{grainId}'.");
        }

        return await directory.TryRenew(
            grainId,
            entry.Version,
            localClusterId,
            _options.ClusterOwnershipLeaseDuration,
            cancellationToken)
            ?? throw new InvalidOperationException($"Ownership lease for grain '{grainId}' could not be renewed.");
    }
}
