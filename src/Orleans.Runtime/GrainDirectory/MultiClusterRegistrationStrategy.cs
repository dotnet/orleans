using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.MultiCluster;

namespace Orleans.GrainDirectory
{
    internal class MultiClusterRegistrationStrategyManager
    {
        public MultiClusterRegistrationStrategyManager(IOptions<MultiClusterOptions> multiClusterOptions)
        {
            var options = multiClusterOptions.Value;
            if (options.HasMultiClusterNetwork && options.UseGlobalSingleInstanceByDefault)
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
}
