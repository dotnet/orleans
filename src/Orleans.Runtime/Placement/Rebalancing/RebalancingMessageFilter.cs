using Orleans.Placement.Rebalancing;
using Orleans.Metadata;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Placement.Rebalancing;

internal interface IRebalancingMessageFilter
{
    bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable);
}

internal sealed class RebalancingMessageFilter : IRebalancingMessageFilter
{
    private readonly GrainManifest _localManifest;
    private readonly PlacementStrategyResolver _strategyResolver;
    private readonly ConcurrentDictionary<uint, bool> _migratableStatuses = new();
    private readonly GrainType _rebalancerGrain = GrainType.Create(IActiveRebalancerGrain.TypeName);

    public RebalancingMessageFilter(
        PlacementStrategyResolver strategyResolver,
        IClusterManifestProvider clusterManifestProvider)
    {
        _strategyResolver = strategyResolver;
        _localManifest = clusterManifestProvider.LocalGrainManifest;
    }

    public bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable)
    {
        isSenderMigratable = false;
        isTargetMigratable = false;

        // Ignore system messages
        if (message.IsSystemMessage)
        {
            return false;
        }

        // It must have a direction, and must not be a 'response' as it would skew analysis.
        if (message.HasDirection is false || message.Direction == Message.Directions.Response)
        {
            return false;
        }

        // Sender and target need to be fully addressible to know where to move to or towards.
        if (!message.IsSenderFullyAddressed || !message.IsTargetFullyAddressed)
        {
            return false;
        }

        // There are some edge cases when this can happen i.e. a grain invoking another one of its methods via AsReference<>, but we still exclude it
        // as wherever this grain would be located in the cluster, it would always be a local call (since it targets itself), this would add negative transfer cost
        // which would skew a potential relocation of this grain, while it shouldn't, because whenever this grain is located, it would still make local calls to itself.
        if (message.SendingGrain == message.TargetGrain)
        {
            return false;
        }

        // Ignore rebalancer messages: either to another rebalancer, or when executing migration requests to activations.
        if (IsRebalancer(message.SendingGrain.Type) || IsRebalancer(message.TargetGrain.Type))
        {
            return false;
        }

        isSenderMigratable = IsMigratable(message.SendingGrain.Type);
        isTargetMigratable = IsMigratable(message.TargetGrain.Type);

        // If both are not migratable types we ignore this. But if one of them is not, than we allow passing, as we wish to move grains closer to them, as with any type of grain.
        if (!isSenderMigratable && !isTargetMigratable)
        {
            return false;
        }

        return true;

        bool IsRebalancer(GrainType grainType) => grainType.Equals(_rebalancerGrain);

        bool IsMigratable(GrainType grainType)
        {
            var hash = grainType.GetUniformHashCode();

            // _migratableStatuses holds statuses for each grain type if its migratable type or not, so we can make fast lookups.
            // since we don't anticipate a huge number of grain *types*, i think its just fine to have this in place as fast-check.
            if (!_migratableStatuses.TryGetValue(hash, out var isMigratable))
            {
                isMigratable = !(grainType.IsClient() || grainType.IsSystemTarget() || grainType.IsGrainService() || IsStatelessWorker(grainType) || IsImmovable(grainType));
                _migratableStatuses.TryAdd(hash, isMigratable);
            }

            return isMigratable;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsStatelessWorker(GrainType grainType) =>
                _strategyResolver.GetPlacementStrategy(grainType).GetType() == typeof(StatelessWorkerPlacement);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            bool IsImmovable(GrainType grainType) =>
                _localManifest.Grains.TryGetValue(grainType, out var props) &&
                props.Properties.TryGetValue(WellKnownGrainTypeProperties.Immovable, out var value) && bool.Parse(value);
        }
    }
}