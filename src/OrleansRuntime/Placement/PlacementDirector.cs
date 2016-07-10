using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal abstract class PlacementDirector : IPlacementDirector
    {
        public abstract Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context);

        public abstract Task<PlacementResult> OnAddActivation(
            PlacementStrategy strategy, GrainId grain, IPlacementContext context);
    }

    internal interface IPlacementDirector
    {
        Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementContext context);

        Task<PlacementResult> OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context);
    }
}
