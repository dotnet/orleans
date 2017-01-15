using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Placement
{
    internal class PlacementDirectorsManager
    {
        private readonly ConcurrentDictionary<Type, IPlacementDirector> directors = new ConcurrentDictionary<Type, IPlacementDirector>();
        private readonly PlacementStrategy defaultPlacementStrategy;
        private readonly ClientObserversPlacementDirector clientObserversPlacementDirector;

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

        public async Task<PlacementResult> SelectOrAddActivation(
                ActivationAddress sendingAddress,
                GrainId targetGrain,
                IPlacementContext context,
                PlacementStrategy strategy)
        {
            if (targetGrain.IsClient)
            {
                var res = await clientObserversPlacementDirector.OnSelectActivation(strategy, targetGrain, context);
                if (res == null)
                {
                    throw new ClientNotAvailableException(targetGrain);
                }
                return res;
            }

            var actualStrategy = strategy ?? defaultPlacementStrategy;
            var result = await SelectActivation(targetGrain, context, actualStrategy);
            if (result != null) return result;

            return await AddActivation(targetGrain, context, actualStrategy);
        }

        private Task<PlacementResult> SelectActivation(
            GrainId targetGrain, 
            IPlacementContext context, 
            PlacementStrategy strategy)
        {
            var director = ResolveDirector(strategy);
            return director.OnSelectActivation(strategy, targetGrain, context);
        }

        private Task<PlacementResult> AddActivation(
                GrainId grain,
                IPlacementContext context,
                PlacementStrategy strategy)
        {
            if (grain.IsClient)
                throw new InvalidOperationException("Client grains are not activated using the placement subsystem.");

            var director = ResolveDirector(strategy);
            return director.OnAddActivation(strategy, grain, context);
        }

        private void ResolveBuiltInStrategies()
        {
            var strategies = new PlacementStrategy[]
            {
                RandomPlacement.Singleton,
                ActivationCountBasedPlacement.Singleton,
                new StatelessWorkerPlacement(),
                PreferLocalPlacement.Singleton
            };
            foreach (var strategy in strategies)
            {
                this.ResolveDirector(strategy);
            }
        }
    }
}
