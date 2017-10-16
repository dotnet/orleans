using System;
using System.Linq;
using System.Reflection;
using Orleans.Runtime.Configuration;
using Orleans.MultiCluster;
using System.Collections.Generic;

namespace Orleans.GrainDirectory
{

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

    public static class MultiClusterRegistrationStrategyExtensions
    {
        public static IEnumerable<string> GetRemoteInstances(this IMultiClusterRegistrationStrategy strategy, MultiClusterConfiguration configuration, string myClusterId)
        {
            return strategy.GetRemoteInstances(configuration.Clusters, myClusterId);
        }
    }
}
