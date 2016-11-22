using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for placement directors.
    /// </summary>
    internal interface IPlacementDirector
    {
        Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context);

        Task<PlacementResult> OnAddActivation(
            PlacementStrategy strategy, GrainId grain, IPlacementContext context);
    }

    /// <summary>
    /// Interface for placement directors implementing the specified strategy.
    /// </summary>
    /// <typeparam name="TStrategy">The placement strategy which this director implements.</typeparam>
    internal interface IPlacementDirector<TStrategy> : IPlacementDirector
        where TStrategy : PlacementStrategy
    {
    }
}
