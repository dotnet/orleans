using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Versions;

namespace Orleans.Streams;

internal sealed class PullingAgentHostResolver(
    IClusterManifestProvider manifestProvider,
    GrainVersionManifest versionManifest,
    IClusterMembershipService membership,
    IInternalGrainFactory grainFactory)
{
    internal Task<SiloAddress[]> GetEligibleSilos(
        string providerName,
        QueueId? queueId,
        GrainType grainType,
        GrainInterfaceType interfaceType,
        CancellationToken cancellationToken)
    {
        var supported = versionManifest.GetSupportedSilos(
            grainType, interfaceType, [versionManifest.GetLocalVersion(interfaceType)]).Result;
        return FilterEligibleSilos(providerName, queueId, grainType, supported.Values.SelectMany(static silos => silos), cancellationToken);
    }

    internal async Task<SiloAddress[]> FilterEligibleSilos(
        string providerName,
        QueueId? queueId,
        GrainType grainType,
        IEnumerable<SiloAddress> compatibleSilos,
        CancellationToken cancellationToken)
    {
        var manifests = manifestProvider.Current.Silos;
        var members = membership.CurrentSnapshot.Members;
        var candidates = compatibleSilos.Where(silo =>
            members.TryGetValue(silo, out var member) && member.Status == SiloStatus.Active
            && manifests.TryGetValue(silo, out var manifest)
            && manifest.Grains.TryGetValue(grainType, out var properties)
            && properties.Properties.ContainsKey(PullingAgentPlacementDirector.ProviderPropertyPrefix + providerName))
            .Distinct().ToArray();
        var eligibility = await Task.WhenAll(candidates.Select(silo => grainFactory
            .GetSystemTarget<IPullingAgentRuntime>(PullingAgentRuntime.TargetType, silo)
            .IsEligible(providerName, queueId, cancellationToken).WaitAsync(cancellationToken)));
        return candidates.Where((_, index) => eligibility[index]).ToArray();
    }
}
