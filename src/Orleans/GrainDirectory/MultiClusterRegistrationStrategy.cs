using System;
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

        internal static void Initialize(GlobalConfiguration config)
        {
            InitializeStrategies();

            if (config.HasMultiClusterNetwork && config.UseGlobalSingleInstanceByDefault)
                defaultStrategy = GlobalSingleInstanceRegistration.Singleton;
            else
                defaultStrategy = ClusterLocalRegistration.Singleton;    
        }
      
        private static void InitializeStrategies()
        {
            ClusterLocalRegistration.Initialize();
            GlobalSingleInstanceRegistration.Initialize();
        }

        internal static MultiClusterRegistrationStrategy GetDefault()
        {
            return defaultStrategy;
        }

        internal abstract bool IsSingleInstance();
    }
}
