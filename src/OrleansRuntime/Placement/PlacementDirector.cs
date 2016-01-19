using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal abstract class PlacementDirector
    {
        internal abstract Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context);

        internal abstract Task<PlacementResult> OnAddActivation(
            PlacementStrategy strategy, GrainId grain, IPlacementContext context);
    }
}
