using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace GrainPlacement;

public interface IOrderWorkerGrain : IGrainWithStringKey
{
    Task ProcessOrder();
}

public interface IOrderCoordinatorGrain : IGrainWithStringKey
{
    Task ProcessOrder(string orderId);

    Task MoveToAnotherSilo();
}

public sealed class OrderWorkerGrain : Grain, IOrderWorkerGrain
{
    public Task ProcessOrder() => Task.CompletedTask;
}

// <direct_placement_with_hint>
public sealed class OrderCoordinatorGrain(
    IClusterMembershipService clusterMembership,
    ILocalSiloDetails localSiloDetails)
    : Grain, IOrderCoordinatorGrain
{
    public async Task ProcessOrder(string orderId)
    {
        var previousHint = RequestContext.Get(
            IPlacementDirector.PlacementHintKey);

        RequestContext.Set(
            IPlacementDirector.PlacementHintKey,
            GetActiveRemoteSilo());

        try
        {
            var worker = GrainFactory.GetGrain<IOrderWorkerGrain>(orderId);
            await worker.ProcessOrder();
        }
        finally
        {
            RestorePlacementHint(previousHint);
        }
    }

    public Task MoveToAnotherSilo()
    {
        var previousHint = RequestContext.Get(
            IPlacementDirector.PlacementHintKey);

        RequestContext.Set(
            IPlacementDirector.PlacementHintKey,
            GetActiveRemoteSilo());

        try
        {
            MigrateOnIdle();
        }
        finally
        {
            RestorePlacementHint(previousHint);
        }

        return Task.CompletedTask;
    }

    private SiloAddress GetActiveRemoteSilo() =>
        clusterMembership.CurrentSnapshot.Members
            .Where(member => member.Value.Status == SiloStatus.Active)
            .Select(member => member.Key)
            .FirstOrDefault(address =>
                !address.Equals(localSiloDetails.SiloAddress))
            ?? throw new InvalidOperationException(
                "No active remote silo is available.");

    private static void RestorePlacementHint(object? previousHint)
    {
        if (previousHint is null)
        {
            RequestContext.Remove(
                IPlacementDirector.PlacementHintKey);
        }
        else
        {
            RequestContext.Set(
                IPlacementDirector.PlacementHintKey,
                previousHint);
        }
    }
}
// </direct_placement_with_hint>
