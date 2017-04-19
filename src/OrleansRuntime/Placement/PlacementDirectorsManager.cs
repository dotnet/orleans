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
            DefaultPlacementStrategy defaultPlacementStrategy,
            ClientObserversPlacementDirector clientObserversPlacementDirector)
        {
            this.serviceProvider = services;
            this.defaultPlacementStrategy = defaultPlacementStrategy.PlacementStrategy;
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
                var directorType = typeof(IPlacementDirector<>).MakeGenericType(strategyType);
                result = (IPlacementDirector)this.serviceProvider.GetRequiredService(directorType);
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
                var directorType = typeof(IActivationSelector<>).MakeGenericType(strategyType);
                result = (IActivationSelector)this.serviceProvider.GetRequiredService(directorType);
                this.selectors[strategyType] = result;
            }

            return result ?? defaultActivationSelector;
        }

        public async Task<PlacementResult> SelectOrAddActivation(
                ActivationAddress sendingAddress,
                PlacementTarget targetGrain,
                IPlacementRuntime context,
                PlacementStrategy strategy)
        {
            if (targetGrain.IsClient)
            {
                var res = await clientObserversPlacementDirector.OnSelectActivation(strategy, (GrainId) targetGrain.GrainIdentity, context);
                if (res == null)
                {
                    throw new ClientNotAvailableException(targetGrain.GrainIdentity);
                }
                return res;
            }

            var actualStrategy = strategy ?? defaultPlacementStrategy;
            var result = await SelectActivation((GrainId) targetGrain.GrainIdentity, context, actualStrategy);
            if (result != null) return result;

            return await AddActivation(targetGrain, context, actualStrategy);
        }

        private Task<PlacementResult> SelectActivation(
            GrainId targetGrain, 
            IPlacementRuntime context, 
            PlacementStrategy strategy)
        {
            var director = ResolveSelector(strategy);
            return director.OnSelectActivation(strategy, targetGrain, context);
        }

        private async Task<PlacementResult> AddActivation(
                PlacementTarget target,
                IPlacementRuntime context,
                PlacementStrategy strategy)
        {
            if (target.IsClient)
                throw new InvalidOperationException("Client grains are not activated using the placement subsystem.");

            var director = ResolveDirector(strategy);
            return PlacementResult.SpecifyCreation(
                await director.OnAddActivation(strategy, target, context), 
                strategy, 
                context.GetGrainTypeName(target.GrainIdentity.TypeCode));
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
