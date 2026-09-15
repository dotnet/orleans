using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Streams;

[GenerateSerializer]
internal sealed class StreamPullingAgentPlacement : PlacementStrategy
{
    internal static TResult WithHint<TResult>(SiloAddress silo, Func<TResult> send)
    {
        var previous = RequestContext.Get(IPlacementDirector.PlacementHintKey);
        RequestContext.Set(IPlacementDirector.PlacementHintKey, silo);
        try
        {
            return send();
        }
        finally
        {
            if (previous is null)
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }
            else
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, previous);
            }
        }
    }
}

internal sealed class StreamPullingAgentPlacementAttribute() : PlacementAttribute(new StreamPullingAgentPlacement());

internal sealed class StreamPullingAgentPlacementDirector(StreamPullingAgentHostResolver hosts) : IPlacementDirector
{
    internal const string ProviderPropertyPrefix = "stream-pulling-agent-provider:";

    public async Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var isCoordinator = target.GrainIdentity.Type == StreamPullingAgentCoordinator.GrainType;
        var (providerName, queueId) = isCoordinator
            ? (target.GrainIdentity.Key.ToString(), (QueueId?)null)
            : GetAgentIdentity(target.GrainIdentity);
        var compatible = context.GetCompatibleSilos(target);
        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatible) is { } hint
            && (await hosts.FilterEligibleSilos(providerName, queueId, target.GrainIdentity.Type, [hint], CancellationToken.None)).Length > 0)
        {
            return hint;
        }

        var eligible = await hosts.FilterEligibleSilos(providerName, queueId, target.GrainIdentity.Type, compatible, CancellationToken.None);
        if (eligible.Length == 0)
        {
            throw new OrleansException($"Stream provider '{providerName}' has no running, compatible host for {target.GrainIdentity}.");
        }

        return eligible[Random.Shared.Next(eligible.Length)];
    }

    private static (string ProviderName, QueueId? QueueId) GetAgentIdentity(GrainId grainId)
    {
        var (providerName, queueId) = StreamPullingAgentId.Parse(grainId);
        return (providerName, queueId);
    }
}
