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

            var selectActivationTask = selector.OnSelectActivation(strategy, target.GrainIdentity, placementRuntime);
            if (selectActivationTask.IsCompletedSuccessfully && selectActivationTask.Result is object)
            {
                return selectActivationTask;
            }

            return GetOrPlaceActivationAsync(selectActivationTask, target, strategy, placementRuntime, director);
        }

        private async ValueTask<PlacementResult> GetOrPlaceActivationAsync(
            ValueTask<PlacementResult> selectActivationTask,
            PlacementTarget target,
            PlacementStrategy strategy,
            IPlacementRuntime placementRuntime,
            IPlacementDirector director)
        {
            var placementResult = await selectActivationTask;
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
