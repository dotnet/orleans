using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Interface for activation selectors.
    /// </summary>
    internal interface IActivationSelector
    {
        ValueTask<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy,
            GrainId target,
            IPlacementRuntime context);
    }
}
