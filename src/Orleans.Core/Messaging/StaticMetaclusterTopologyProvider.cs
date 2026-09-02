using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime;

internal sealed class StaticMetaclusterTopologyProvider : IMetaclusterTopologyProvider
{
    private readonly MetaclusterTopology _topology;

    public StaticMetaclusterTopologyProvider(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<MetaclusterOptions> metaclusterOptions)
    {
        if (!metaclusterOptions.Value.Enabled)
        {
            var serviceId = string.IsNullOrWhiteSpace(clusterOptions.Value.ServiceId)
                ? ClusterOptions.DefaultServiceId
                : clusterOptions.Value.ServiceId;
            var clusterId = string.IsNullOrWhiteSpace(clusterOptions.Value.ClusterId)
                ? ClusterOptions.DefaultClusterId
                : clusterOptions.Value.ClusterId;
            _topology = new MetaclusterTopology(
                serviceId,
                epoch: 0,
                ImmutableDictionary<string, MetaclusterCluster>.Empty
                    .WithComparers(System.StringComparer.Ordinal)
                    .Add(clusterId, new MetaclusterCluster(clusterId, MetaclusterClusterState.Active, [])));
            return;
        }

        if (string.IsNullOrWhiteSpace(clusterOptions.Value.ServiceId)
            || string.IsNullOrWhiteSpace(clusterOptions.Value.ClusterId))
        {
            throw new OrleansConfigurationException(
                $"Valid {nameof(ClusterOptions.ServiceId)} and {nameof(ClusterOptions.ClusterId)} values are required for metacluster topology.");
        }

        var clusters = ImmutableDictionary.CreateBuilder<string, MetaclusterCluster>(System.StringComparer.Ordinal);
        foreach (var entry in metaclusterOptions.Value.Clusters)
        {
            if (entry.Value is null)
            {
                throw new OrleansConfigurationException(
                    $"Relay endpoints for cluster '{entry.Key}' must be initialized.");
            }

            clusters[entry.Key] = new MetaclusterCluster(
                entry.Key,
                MetaclusterClusterState.Active,
                [.. entry.Value]);
        }

        if (!clusters.ContainsKey(clusterOptions.Value.ClusterId))
        {
            clusters[clusterOptions.Value.ClusterId] = new MetaclusterCluster(
                clusterOptions.Value.ClusterId,
                MetaclusterClusterState.Active,
                []);
        }

        _topology = new MetaclusterTopology(clusterOptions.Value.ServiceId, epoch: 0, clusters.ToImmutable());
    }

    public ValueTask<MetaclusterTopology> GetTopology(CancellationToken cancellationToken = default)
        => new(_topology);

    public async IAsyncEnumerable<MetaclusterTopology> Watch(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return _topology;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
