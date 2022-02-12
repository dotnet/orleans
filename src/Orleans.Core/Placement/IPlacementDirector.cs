using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for placement directors.
    /// </summary>
    public interface IPlacementDirector
    {
        /// <summary>
        /// Picks an appropriate silo to place the specified target on.
        /// </summary>
        /// <param name="strategy">The target's placement strategy.</param>
        /// <param name="target">The grain being placed as well as information about the request which triggered the placement.</param>
        /// <param name="context">The placement context.</param>
        /// <returns>An appropriate silo to place the specified target on.</returns>
        Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context);
    }
}
