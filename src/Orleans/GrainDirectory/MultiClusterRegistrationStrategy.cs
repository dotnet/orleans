using System;
using System.Linq;
using Orleans.Runtime.Configuration;
using Orleans.MultiCluster;
using System.Collections.Generic;

namespace Orleans.GrainDirectory
{

    /// <summary>
    /// Interface for multi-cluster registration strategies. Used by protocols that coordinate multiple instances.
    /// </summary>
    public interface IMultiClusterRegistrationStrategy {

        /// <summary>
        /// Determines which remote clusters have instances.
        /// </summary>
        /// <param name="mcConfig">The multi-cluster configuration</param>
        /// <param name="myClusterId">The cluster id of this cluster</param>
        /// <returns></returns>
        IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId);

    }

    /// <summary>
    /// A superclass for all multi-cluster registration strategies.
    /// Strategy object which is used as keys to select the proper registrar.
    /// </summary>
    [Serializable]
    internal abstract class MultiClusterRegistrationStrategy : IMultiClusterRegistrationStrategy
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

        internal static MultiClusterRegistrationStrategy FromAttributes(IEnumerable<object> attributes)
        {
            return (attributes.FirstOrDefault() as RegistrationAttribute)?.RegistrationStrategy ?? defaultStrategy;
        }

        public abstract IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId);
    }
}
