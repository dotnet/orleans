using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime.Configuration;

namespace Orleans.Streams
{
    /// <summary>
    /// Deployment configuration that reads from orleans cluster configuration
    /// </summary>
    internal class StaticClusterDeploymentConfiguration : IDeploymentConfiguration
    {
        private readonly ClusterConfiguration _clusterConfiguration;

        public StaticClusterDeploymentConfiguration(ClusterConfiguration clusterConfiguration)
        {
            if (clusterConfiguration == null)
            {
                throw new ArgumentNullException("clusterConfiguration");
            }
            _clusterConfiguration = clusterConfiguration;
        }

        public IList<string> GetAllSiloNames()
        {
            return _clusterConfiguration.Overrides.Keys.ToList();
        }
    }
}
