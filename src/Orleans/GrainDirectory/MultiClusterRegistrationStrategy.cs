using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// A superclass for all multi-cluster registration strategies.
    /// Strategy objects are used as keys to select the proper registrar.
    /// </summary>
    [Serializable]
    internal abstract class MultiClusterRegistrationStrategy
    {
        private static MultiClusterRegistrationStrategy defaultStrategy;

        internal static void Initialize(GlobalConfiguration config = null)
        {
            InitializeStrategies();
            var strategy = config == null
                ? GlobalConfiguration.DEFAULT_MULTICLUSTER_REGISTRATION_STRATEGY
                : config.DefaultMultiClusterRegistrationStrategy;
            defaultStrategy = GetStrategy(strategy);
        }
        
        private static MultiClusterRegistrationStrategy GetStrategy(string strategy)
        {
            if (strategy.Equals(typeof (ClusterLocalRegistration).Name))
            {
                return ClusterLocalRegistration.Singleton;
            }
            return null;
        }

        private static void InitializeStrategies()
        {
            ClusterLocalRegistration.Initialize();
        }

        internal static MultiClusterRegistrationStrategy GetDefault()
        {
            return defaultStrategy;
        }

        internal abstract bool IsSingleInstance();
    }
}
