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
}

public sealed class OrderWorkerGrain : Grain, IOrderWorkerGrain
{
    public Task ProcessOrder() => Task.CompletedTask;
}

public sealed class OrderCoordinatorGrain : Grain, IOrderCoordinatorGrain
{
    // <direct_placement_with_hint>
    public async Task ProcessOrder(string orderId)
    {
        var worker = GrainFactory.GetGrain<IOrderWorkerGrain>(orderId);
        var previousHint = RequestContext.Get(
            IPlacementDirector.PlacementHintKey);

        RequestContext.Set(
            IPlacementDirector.PlacementHintKey,
            GrainContext.Address.SiloAddress!);

        try
        {
            await worker.ProcessOrder();
        }
        finally
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
}
