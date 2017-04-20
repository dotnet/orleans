using System;
using System.Linq;
using System.Reflection;
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

    internal class MultiClusterRegistrationStrategyManager
    {
        public MultiClusterRegistrationStrategyManager(GlobalConfiguration config)
        {
            if (config.HasMultiClusterNetwork && config.UseGlobalSingleInstanceByDefault)
            {
                this.DefaultStrategy = GlobalSingleInstanceRegistration.Singleton;
            }
            else
            {
                this.DefaultStrategy = ClusterLocalRegistration.Singleton;
            }
        }

        public MultiClusterRegistrationStrategy DefaultStrategy { get; }

        internal MultiClusterRegistrationStrategy GetMultiClusterRegistrationStrategy(Type grainClass)
        {
            var attribs = grainClass.GetTypeInfo().GetCustomAttributes<RegistrationAttribute>(inherit: true).ToArray();

            switch (attribs.Length)
            {
                case 0:
                    return this.DefaultStrategy; // no strategy is specified
                case 1:
                    return attribs[0].RegistrationStrategy ?? this.DefaultStrategy;
                default:
                    throw new InvalidOperationException(
                        string.Format(
                            "More than one {0} cannot be specified for grain interface {1}",
                            typeof(MultiClusterRegistrationStrategy).Name,
                            grainClass.Name));
            }
        }
    }

    /// <summary>
    /// A superclass for all multi-cluster registration strategies.
    /// Strategy object which is used as keys to select the proper registrar.
    /// </summary>
    [Serializable]
    internal abstract class MultiClusterRegistrationStrategy : IMultiClusterRegistrationStrategy
    {
        public abstract IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId);
    }
}
