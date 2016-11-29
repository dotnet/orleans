using System;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    [Serializable]
    internal abstract class PlacementStrategy
    {
    }

    internal class DefaultPlacementStrategy
    {
        public DefaultPlacementStrategy(GlobalConfiguration config) : this(config.DefaultPlacementStrategy)
        {
        }

        protected DefaultPlacementStrategy(string placement)
        {
            this.PlacementStrategy = GetDefaultStrategy(placement);
        }

        /// <summary>
        /// Gets the default placement strategy.
        /// </summary>
        public PlacementStrategy PlacementStrategy { get; }
        
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
