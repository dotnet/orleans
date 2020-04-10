using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement
{
    internal class PlacementDirectorsManager
    {
        private readonly ConcurrentDictionary<Type, IPlacementDirector> directors = new ConcurrentDictionary<Type, IPlacementDirector>();
        private readonly ConcurrentDictionary<Type, IActivationSelector> selectors = new ConcurrentDictionary<Type, IActivationSelector>();
        private readonly PlacementStrategy defaultPlacementStrategy;
        private readonly ClientObserversPlacementDirector clientObserversPlacementDirector;
        private readonly IActivationSelector defaultActivationSelector;

        private readonly IServiceProvider serviceProvider;

        public PlacementDirectorsManager(
            IServiceProvider services,
            PlacementStrategy defaultPlacementStrategy,
            ClientObserversPlacementDirector clientObserversPlacementDirector)
        {
            this.serviceProvider = services;
            this.defaultPlacementStrategy = defaultPlacementStrategy;
            this.clientObserversPlacementDirector = clientObserversPlacementDirector;
            this.ResolveBuiltInStrategies();
            // TODO: Make default selector configurable
            this.defaultActivationSelector = ResolveSelector(RandomPlacement.Singleton, true);
        }

        private IPlacementDirector ResolveDirector(PlacementStrategy strategy)
        {
            IPlacementDirector result;
            var strategyType = strategy.GetType();
            if (!this.directors.TryGetValue(strategyType, out result))
            {
                result = this.serviceProvider.GetRequiredServiceByKey<Type, IPlacementDirector>(strategyType);
                this.directors[strategyType] = result;
            }

            return result;
        }

        private IActivationSelector ResolveSelector(PlacementStrategy strategy, bool addIfDoesNotExist = false)
        {
            IActivationSelector result;
            var strategyType = strategy.GetType();
            if (!this.selectors.TryGetValue(strategyType, out result) && addIfDoesNotExist)
            {
                result = this.serviceProvider.GetRequiredServiceByKey<Type, IActivationSelector>(strategyType);
                this.selectors[strategyType] = result;
            }

            return result ?? defaultActivationSelector;
        }

        public bool TrySelectActivationSynchronously(
                ActivationAddress sendingAddress,
                PlacementTarget targetGrain,
                IPlacementRuntime context,
                PlacementStrategy strategy,
                out PlacementResult placementResult)
        {
            if (targetGrain.IsClient)
            {
                return clientObserversPlacementDirector.TrySelectActivationSynchronously(
                    strategy, 
                    (GrainId) targetGrain.GrainIdentity, 
                    context, 
                    out placementResult);
            }

            var actualStrategy = strategy ?? defaultPlacementStrategy;
            var director = ResolveSelector(actualStrategy);
            return director.TrySelectActivationSynchronously(strategy, (GrainId)targetGrain.GrainIdentity, context, out placementResult);
        }

        public async Task<PlacementResult> SelectOrAddActivation(
            PlacementTarget targetGrain,
            IPlacementRuntime context,
            PlacementStrategy strategy)
        {
            if (targetGrain.IsClient)
            {
                var res = await clientObserversPlacementDirector.OnSelectActivation(strategy, (GrainId)targetGrain.GrainIdentity, context);
                if (res == null)
                {
                    throw new ClientNotAvailableException(targetGrain.GrainIdentity);
                }
                return res;
            }

            var actualStrategy = strategy ?? defaultPlacementStrategy;
            var director = ResolveSelector(actualStrategy);
            var result = await director.OnSelectActivation(strategy, targetGrain.GrainIdentity, context);
            if (result != null) return result;

            return await AddActivation(targetGrain, context, actualStrategy);
        }

        private async Task<PlacementResult> AddActivation(
                PlacementTarget target,
                IPlacementRuntime context,
                PlacementStrategy strategy)
        {
            if (target.IsClient)
                throw new InvalidOperationException("Client grains are not activated using the placement subsystem.");

            var director = ResolveDirector(strategy);
            var siloAddress = await director.OnAddActivation(strategy, target, context);
            var grainTypeName = context.GetGrainTypeName(((LegacyGrainId)target.GrainIdentity).TypeCode);

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
                strategy,
                grainTypeName);
        }

        private void ResolveBuiltInStrategies()
        {
            var statelessWorker = new StatelessWorkerPlacement();

            var placementStrategies = new PlacementStrategy[]
            {
                RandomPlacement.Singleton,
                ActivationCountBasedPlacement.Singleton,
                statelessWorker,
                PreferLocalPlacement.Singleton
            };

            foreach (var strategy in placementStrategies)
                this.ResolveDirector(strategy);
            
            var selectorStrategies = new PlacementStrategy[]
            {
                RandomPlacement.Singleton,
                statelessWorker,
            };

            foreach (var strategy in selectorStrategies)
                this.ResolveSelector(strategy, true);
        }
    }
}
