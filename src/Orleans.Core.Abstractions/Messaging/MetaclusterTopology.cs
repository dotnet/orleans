using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Describes the clusters which form an Orleans metacluster.
/// </summary>
[GenerateSerializer, Immutable]
public sealed class MetaclusterTopology
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetaclusterTopology"/> class.
    /// </summary>
    public MetaclusterTopology(string serviceId, long epoch, ImmutableDictionary<string, MetaclusterCluster> clusters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(clusters);
        if (epoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        ServiceId = serviceId;
        Epoch = epoch;
        Clusters = clusters.WithComparers(StringComparer.Ordinal);
        foreach (var cluster in Clusters)
        {
            if (cluster.Value is null)
            {
                throw new ArgumentException($"Topology entry '{cluster.Key}' has no cluster descriptor.", nameof(clusters));
            }

            if (!string.Equals(cluster.Key, cluster.Value.ClusterId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Topology key '{cluster.Key}' does not match cluster identity '{cluster.Value.ClusterId}'.",
                    nameof(clusters));
            }
        }
    }

    /// <summary>
    /// Gets the Orleans service identity.
    /// </summary>
    [Id(0)]
    public string ServiceId { get; }

    /// <summary>
    /// Gets the topology epoch.
    /// </summary>
    [Id(1)]
    public long Epoch { get; }

    /// <summary>
    /// Gets clusters by cluster identity.
    /// </summary>
    [Id(2)]
    public ImmutableDictionary<string, MetaclusterCluster> Clusters { get; }
}

/// <summary>
/// Describes a cluster in a metacluster.
/// </summary>
[GenerateSerializer, Immutable]
public sealed class MetaclusterCluster
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetaclusterCluster"/> class.
    /// </summary>
    public MetaclusterCluster(
        string clusterId,
        MetaclusterClusterState state,
        ImmutableArray<Uri> relayEndpoints,
        ImmutableDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ClusterId = clusterId;
        State = state;
        RelayEndpoints = relayEndpoints.IsDefault ? [] : relayEndpoints;
        foreach (var endpoint in RelayEndpoints)
        {
            if (endpoint is null || !endpoint.IsAbsoluteUri)
            {
                throw new ArgumentException("Relay endpoints must be absolute URIs.", nameof(relayEndpoints));
            }
        }

        Metadata = (metadata ?? ImmutableDictionary<string, string>.Empty).WithComparers(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the cluster identity.
    /// </summary>
    [Id(0)]
    public string ClusterId { get; }

    /// <summary>
    /// Gets the administrative cluster state.
    /// </summary>
    [Id(1)]
    public MetaclusterClusterState State { get; }

    /// <summary>
    /// Gets relay endpoints for the cluster.
    /// </summary>
    [Id(2)]
    public ImmutableArray<Uri> RelayEndpoints { get; }

    /// <summary>
    /// Gets cluster metadata.
    /// </summary>
    [Id(3)]
    public ImmutableDictionary<string, string> Metadata { get; }
}

/// <summary>
/// Describes the administrative state of a metacluster member.
/// </summary>
[GenerateSerializer]
public enum MetaclusterClusterState : byte
{
    /// <summary>
    /// The cluster can receive federated requests.
    /// </summary>
    Active,

    /// <summary>
    /// The cluster is draining existing ownership and does not receive new placements.
    /// </summary>
    Draining,

    /// <summary>
    /// The cluster has been removed from the active federation.
    /// </summary>
    Removed
}

/// <summary>
/// Provides authoritative metacluster topology views.
/// </summary>
public interface IMetaclusterTopologyProvider
{
    /// <summary>
    /// Gets the current topology.
    /// </summary>
    ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns topology updates.
    /// </summary>
    IAsyncEnumerable<MetaclusterTopology> Watch(CancellationToken cancellationToken = default);
}
