using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for placement directors.
    /// </summary>
    public interface IPlacementDirector
    {
        Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context);
    }

    /// <summary>
    /// Interface for placement directors implementing the specified strategy.
    /// </summary>
    /// <typeparam name="TStrategy">The placement strategy which this director implements.</typeparam>
    public interface IPlacementDirector<TStrategy> : IPlacementDirector
        where TStrategy : PlacementStrategy
    {
    }
}
