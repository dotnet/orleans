using System;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    [Serializable]
    internal abstract class PlacementStrategy
    {
        private static PlacementStrategy defaultStrategy;

        internal static void Initialize()
        {
            InitializePlacements();
            defaultStrategy = GetDefaultStrategy(GlobalConfiguration.DEFAULT_PLACEMENT_STRATEGY);
        }

        internal static void Initialize(GlobalConfiguration config)
        {
            InitializePlacements();
            GrainStrategy.InitDefaultGrainStrategies();
            defaultStrategy = GetDefaultStrategy(config.DefaultPlacementStrategy);
        }

        internal static PlacementStrategy GetDefault()
        {
            return defaultStrategy;
        }

        private static void InitializePlacements()
        {
            RandomPlacement.InitializeClass();
            PreferLocalPlacement.InitializeClass();
            StatelessWorkerPlacement.InitializeClass(NodeConfiguration.DEFAULT_MAX_LOCAL_ACTIVATIONS);
            SystemPlacement.InitializeClass();
            ActivationCountBasedPlacement.InitializeClass();
        }

        private static PlacementStrategy GetDefaultStrategy(string str)
        {
            if (str.Equals(typeof(RandomPlacement).Name))
            {
                return RandomPlacement.Singleton;
            }
            else if (str.Equals(typeof(PreferLocalPlacement).Name))
            {
                return PreferLocalPlacement.Singleton;
            }
            else if (str.Equals(typeof(SystemPlacement).Name))
            {
                return SystemPlacement.Singleton;
            }
            else if (str.Equals(typeof(ActivationCountBasedPlacement).Name))
            {
                return ActivationCountBasedPlacement.Singleton;
            }
            return null;
        }
    }
}
