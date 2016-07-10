using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Placement
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;

    internal class PlacementDirectorsManager
    {
        private readonly IServiceProvider serviceProvider;
        private readonly PlacementStrategy defaultPlacementStrategy;
        private readonly ClientObserversPlacementDirector clientObserversPlacementDirector;
        
        public PlacementDirectorsManager(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
            defaultPlacementStrategy = PlacementStrategy.GetDefault();
            clientObserversPlacementDirector = new ClientObserversPlacementDirector();
        }

        public static void ConfigurePlacementDirectors(IServiceCollection serviceCollection)
        {
            serviceCollection.TryAddSingleton<IPlacementDirector<RandomPlacement>, RandomPlacementDirector>();
            serviceCollection.TryAddSingleton<IPlacementDirector<PreferLocalPlacement>, PreferLocalPlacementDirector>();
            serviceCollection.TryAddSingleton<IPlacementDirector<StatelessWorkerPlacement>, StatelessWorkerDirector>();
            serviceCollection.TryAddSingleton<IPlacementDirector<ActivationCountBasedPlacement>, ActivationCountPlacementDirector>();
            serviceCollection.TryAddSingleton<PlacementDirectorsManager>();
        }
        
        private IPlacementDirector ResolveDirector(PlacementStrategy strategy)
        {
            return this.serviceProvider.GetRequiredService(strategy.DirectorType) as IPlacementDirector;
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
    }
}
