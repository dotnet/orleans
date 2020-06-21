using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Central point for placement decisions.
    /// </summary>
    internal class PlacementService
    {
        private readonly PlacementStrategyResolver _strategyResolver;
        private readonly PlacementDirectorResolver _directorResolver;
        private readonly RandomPlacementDirector _defaultActivationSelector = new RandomPlacementDirector();

        /// <summary>
        /// Create a <see cref="PlacementService"/> instance.
        /// </summary>
        public PlacementService(PlacementDirectorResolver directorResolver, PlacementStrategyResolver strategyResolver)
        {
            _strategyResolver = strategyResolver;
            _directorResolver = directorResolver;
        }

        /// <summary>
        /// Gets or places an activation.
        /// </summary>
        public ValueTask<PlacementResult> GetOrPlaceActivation(PlacementTarget target, IPlacementRuntime placementRuntime)
        {
            var strategy = _strategyResolver.GetPlacementStrategy(target.GrainIdentity.Type);
            var director = _directorResolver.GetPlacementDirector(strategy);
            var selector = director as IActivationSelector ?? _defaultActivationSelector;

            if (selector.TrySelectActivationSynchronously(strategy, target.GrainIdentity, placementRuntime, out var placementResult))
            {
                return new ValueTask<PlacementResult>(placementResult);
            }

            return GetOrPlaceActivationAsync(target, strategy, placementRuntime, selector, director);
        }

        private async ValueTask<PlacementResult> GetOrPlaceActivationAsync(
            PlacementTarget target,
            PlacementStrategy strategy,
            IPlacementRuntime placementRuntime,
            IActivationSelector selector,
            IPlacementDirector director)
        {
            var placementResult = await selector.OnSelectActivation(strategy, target.GrainIdentity, placementRuntime);
            if (placementResult is object)
            {
                return placementResult;
            }

            var siloAddress = await director.OnAddActivation(strategy, target, placementRuntime);

            ActivationId activationId;
            if (strategy.IsDeterministicActivationId)
            {
                // Use the grain id as the activation id.
                activationId = ActivationId.GetDeterministic(target.GrainIdentity);
            }
            else
            {
                activationId = ActivationId.NewId();
            }

            return PlacementResult.SpecifyCreation(
                siloAddress,
                activationId,
                strategy);
        }
    }
}
