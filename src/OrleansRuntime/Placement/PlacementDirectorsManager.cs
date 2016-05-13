using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Placement
{
    internal class PlacementDirectorsManager
    {
        private readonly Dictionary<Type, PlacementDirector> directors = new Dictionary<Type, PlacementDirector>();
        private PlacementStrategy defaultPlacementStrategy;
        private ClientObserversPlacementDirector clientObserversPlacementDirector;

        public static PlacementDirectorsManager Instance { get; private set; }

        private PlacementDirectorsManager()
        { }

        public static void CreatePlacementDirectorsManager(GlobalConfiguration globalConfig)
        {
            Instance = new PlacementDirectorsManager();
            Instance.Register<RandomPlacement, RandomPlacementDirector>();
            Instance.Register<PreferLocalPlacement, PreferLocalPlacementDirector>();
            Instance.Register<StatelessWorkerPlacement, StatelessWorkerDirector>();
            Instance.Register<ActivationCountBasedPlacement, ActivationCountPlacementDirector>();

            var acDirector = (ActivationCountPlacementDirector)Instance.directors[typeof(ActivationCountBasedPlacement)];
            acDirector.Initialize(globalConfig);

            Instance.defaultPlacementStrategy = PlacementStrategy.GetDefault();
            Instance.clientObserversPlacementDirector = new ClientObserversPlacementDirector();
        }

        private void Register<TStrategy, TDirector>()
            where TDirector : PlacementDirector, new()
            where TStrategy : PlacementStrategy
        {
            directors.Add(typeof(TStrategy), new TDirector());
        }

        private PlacementDirector ResolveDirector(PlacementStrategy strategy)
        {
            return directors[strategy.GetType()];
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
