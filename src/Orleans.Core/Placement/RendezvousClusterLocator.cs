using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement;

/// <summary>
/// Deterministically maps grains to active clusters using rendezvous hashing.
/// </summary>
public sealed class RendezvousClusterLocator(IMetaclusterTopologyProvider topologyProvider) : IClusterLocator
{
    /// <inheritdoc/>
    public async ValueTask<ClusterLocation> Locate(
        GrainId grainId,
        ClusterLocationContext context,
        CancellationToken cancellationToken = default)
    {
        var topology = await topologyProvider.GetTopology(cancellationToken);
        if (!string.Equals(topology.ServiceId, context.ServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Topology service '{topology.ServiceId}' does not match reference service '{context.ServiceId}'.");
        }

        string? selectedCluster = null;
        uint selectedScore = 0;
        foreach (var cluster in topology.Clusters.Values)
        {
            if (cluster.State != MetaclusterClusterState.Active)
            {
                continue;
            }

            var score = StableHash.ComputeHash($"{grainId.GetUniformHashCode():X8}:{cluster.ClusterId}");
            if (selectedCluster is null
                || score > selectedScore
                || (score == selectedScore && string.CompareOrdinal(cluster.ClusterId, selectedCluster) < 0))
            {
                selectedCluster = cluster.ClusterId;
                selectedScore = score;
            }
        }

        return selectedCluster is null
            ? throw new InvalidOperationException($"Metacluster topology epoch '{topology.Epoch}' has no active clusters.")
            : new ClusterLocation(
                selectedCluster,
                Version: topology.Epoch,
                TopologyEpoch: topology.Epoch,
                IsExistingOwner: false);
    }
}
