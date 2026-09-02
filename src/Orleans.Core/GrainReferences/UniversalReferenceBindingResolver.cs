using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.GrainReferences;

internal sealed class UniversalReferenceBindingResolver
{
    private readonly ClusterOptions _clusterOptions;
    private readonly MetaclusterOptions _metaclusterOptions;
    private readonly GrainPropertiesResolver _grainPropertiesResolver;

    public UniversalReferenceBindingResolver(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<MetaclusterOptions> metaclusterOptions,
        GrainPropertiesResolver grainPropertiesResolver)
    {
        _clusterOptions = clusterOptions.Value;
        _metaclusterOptions = metaclusterOptions.Value;
        _grainPropertiesResolver = grainPropertiesResolver;
    }

    public string ServiceId => _clusterOptions.ServiceId;

    public string ClusterId => _clusterOptions.ClusterId;

    public bool IsMetaclusterEnabled => _metaclusterOptions.Enabled;

    public ClusterIdentity LocalCluster => new(ServiceId, ClusterId);

    public UniversalReferenceBinding GetBinding(GrainType grainType)
    {
        if (!_metaclusterOptions.Enabled)
        {
            return UniversalReferenceBinding.Virtual;
        }

        if (grainType.IsClient() || grainType.IsSystemTarget())
        {
            return UniversalReferenceBinding.Cluster;
        }

        if (!_grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties))
        {
            throw new InvalidOperationException(
                $"Grain properties for type '{grainType}' must be available before creating a metacluster reference.");
        }

        return properties.Properties.TryGetValue(WellKnownGrainTypeProperties.ClusterLocator, out var locator)
            && !string.IsNullOrWhiteSpace(locator)
            ? UniversalReferenceBinding.Virtual
            : UniversalReferenceBinding.Cluster;
    }
}
