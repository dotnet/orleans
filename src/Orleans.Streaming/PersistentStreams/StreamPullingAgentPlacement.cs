using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Streams;

[GenerateSerializer]
internal sealed class StreamPullingAgentPlacement : PlacementStrategy;

internal sealed class StreamPullingAgentPlacementAttribute() : PlacementAttribute(new StreamPullingAgentPlacement());

internal sealed class StreamPullingAgentPlacementDirector(
    IClusterManifestProvider manifestProvider,
    IInternalGrainFactory grainFactory) : IPlacementDirector
{
    internal const string ProviderPropertyPrefix = "stream-pulling-agent-provider:";

    public async Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var (providerName, queueId) = StreamPullingAgentId.Parse(target.GrainIdentity);
        var manifests = manifestProvider.Current.Silos;
        var candidates = context.GetCompatibleSilos(target)
            .Where(silo => manifests.TryGetValue(silo, out var manifest)
                && manifest.Grains.TryGetValue(StreamPullingAgentId.GrainType, out var properties)
                && properties.Properties.ContainsKey(ProviderPropertyPrefix + providerName))
            .ToArray();
        var eligibility = await Task.WhenAll(candidates.Select(silo => grainFactory
            .GetSystemTarget<IStreamPullingAgentRuntime>(StreamPullingAgentRuntime.TargetType, silo)
            .IsEligible(providerName, queueId)));
        var eligible = candidates.Where((_, index) => eligibility[index]).ToArray();
        if (eligible.Length == 0)
        {
            throw new OrleansException($"Stream provider '{providerName}' has no running, assigned host for queue {queueId:H}.");
        }

        return IPlacementDirector.GetPlacementHint(target.RequestContextData, eligible)
            ?? eligible.OrderBy(static silo => silo).First();
    }
}
