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
}
