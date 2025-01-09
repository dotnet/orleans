#nullable enable

using Orleans.Placement;

namespace Orleans.Runtime.Placement.Repartitioning;

internal interface IRepartitionerMessageFilter
{
    bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable);
}

internal sealed class RepartitionerMessageFilter(GrainMigratabilityChecker checker) : IRepartitionerMessageFilter
{
    public bool IsAcceptable(Message message, out bool isSenderMigratable, out bool isTargetMigratable)
    {
        isSenderMigratable = false;
        isTargetMigratable = false;

        // There are some edge cases when this can happen i.e. a grain invoking another one of its methods via AsReference<>, but we still exclude it
        // as wherever this grain would be located in the cluster, it would always be a local call (since it targets itself), this would add negative transfer cost
        // which would skew a potential relocation of this grain, while it shouldn't, because whenever this grain is located, it would still make local calls to itself.
        if (message.SendingGrain == message.TargetGrain)
        {
            return false;
        }

        isSenderMigratable = checker.IsMigratable(message.SendingGrain.Type, ImmovableKind.Repartitioner);
        isTargetMigratable = checker.IsMigratable(message.TargetGrain.Type, ImmovableKind.Repartitioner);

        // If both are not migratable types we ignore this. But if one of them is not, then we allow passing, as we wish to move grains closer to them, as with any type of grain.
        return isSenderMigratable || isTargetMigratable;
    }
}