using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace GrainPlacement;

public interface IOrderWorkerGrain : IGrainWithStringKey
{
    Task ProcessOrder(string orderId);
}

public interface IOrderAuditGrain : IGrainWithStringKey
{
    Task RecordProcessedOrder(string orderId);
}

public interface IOrderCoordinatorGrain : IGrainWithStringKey
{
    Task ProcessOrder(string orderId);

    Task MoveToAnotherSilo();

    Task MoveToAnotherSiloWithExplicitContext();
}

public sealed class OrderAuditGrain : Grain, IOrderAuditGrain
{
    public Task RecordProcessedOrder(string orderId) =>
        Task.CompletedTask;
}

// <contain_received_placement_hint>
public sealed class OrderWorkerGrain : Grain, IOrderWorkerGrain
{
    public async Task ProcessOrder(string orderId)
    {
        var placementHint = RequestContext.Get(
            IPlacementDirector.PlacementHintKey);

        RequestContext.Remove(
            IPlacementDirector.PlacementHintKey);

        try
        {
            var audit = GrainFactory.GetGrain<IOrderAuditGrain>("orders");
            await audit.RecordProcessedOrder(orderId);
        }
        finally
        {
            if (placementHint is not null)
            {
                RequestContext.Set(
                    IPlacementDirector.PlacementHintKey,
                    placementHint);
            }
        }
    }
}
// </contain_received_placement_hint>

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
            await worker.ProcessOrder(orderId);
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

    public Task MoveToAnotherSiloWithExplicitContext()
    {
        GrainContext.Migrate(new Dictionary<string, object>
        {
            [IPlacementDirector.PlacementHintKey] =
                GetActiveRemoteSilo()
        });

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
