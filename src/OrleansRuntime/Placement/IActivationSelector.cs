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
    }

    /// <summary>
    /// Interface for activation selectorsimplementing the specified strategy.
    /// </summary>
    /// <typeparam name="TStrategy">The placement strategy which this selector implements.</typeparam>
    internal interface IActivationSelector<TStrategy> : IActivationSelector
        where TStrategy : PlacementStrategy
    {
    }
}
