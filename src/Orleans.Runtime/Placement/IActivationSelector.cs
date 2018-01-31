
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for activation selectors.
    /// </summary>
    internal interface IActivationSelector
    {
        Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime context);

        bool TrySelectActivationSynchronously(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime context, out PlacementResult placementResult);
    }
}
